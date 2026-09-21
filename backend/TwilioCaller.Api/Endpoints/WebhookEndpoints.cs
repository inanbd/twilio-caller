using Microsoft.EntityFrameworkCore;
using Twilio.Security;
using Twilio.TwiML;
using Dial = Twilio.TwiML.Voice.Dial;
using Twilio.Types;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Realtime;
using TwilioCaller.Api.Services;

namespace TwilioCaller.Api.Endpoints;

/// <summary>
/// Endpoints Twilio itself calls. These are anonymous as far as our session tokens go
/// — Twilio has no bearer token — so they are guarded by the per-connection webhook
/// key in the path and, when an auth token is on file, an X-Twilio-Signature check.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks/{connectionId}/{webhookKey}")
            .AllowAnonymous()
            .WithTags("Webhooks");

        group.MapPost("/voice", HandleVoiceAsync);
        group.MapPost("/voice/status", HandleVoiceStatusAsync);
        group.MapPost("/sms", HandleSmsAsync);
        group.MapPost("/sms/status", HandleSmsStatusAsync);
    }

    /// <summary>
    /// Single entry point for both call directions. Twilio identifies a call placed by
    /// a Voice SDK client with a "client:" prefixed From, which is how we tell an
    /// outbound app call apart from someone dialling one of the account's numbers.
    /// </summary>
    private static async Task<IResult> HandleVoiceAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        AppDbContext db,
        ConnectionService connections,
        WebhookUrlBuilder urls,
        RealtimeNotifier notifier,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Webhooks.Voice");
        var (connection, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        var from = form!["From"].ToString();
        var to = form["To"].ToString();
        var callSid = form["CallSid"].ToString();
        var response = new VoiceResponse();

        if (from.StartsWith("client:", StringComparison.OrdinalIgnoreCase))
        {
            // Outbound: the app passed the destination and which of its numbers to
            // show as caller id when it called Device.connect().
            var destination = Value(form, "To", "Destination");
            var callerId = Value(form, "CallerId", "From_Number");

            if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(callerId))
            {
                logger.LogWarning(
                    "Outbound call {CallSid} missing destination or caller id", callSid);
                response.Say("The call could not be placed because it was missing a destination.");
                return TwiML(response);
            }

            var dial = new Dial(callerId: callerId, answerOnBridge: true);
            dial.Number(new PhoneNumber(destination));
            response.Append(dial);
            return TwiML(response);
        }

        // Inbound: ring every app instance that can actually run the Voice SDK.
        var identities = await connections.ActiveVoipIdentitiesAsync(connectionId);

        await RecordAndNotifyAsync(db, notifier, connectionId, new InboundEvent
        {
            ConnectionId = connectionId,
            Kind = "call",
            TwilioSid = callSid,
            FromNumber = from,
            ToNumber = to,
            Status = form["CallStatus"].ToString(),
        }, isCall: true);

        if (identities.Count > 0)
        {
            var dial = new Dial(timeout: 30, answerOnBridge: true, callerId: from);
            foreach (var identity in identities)
            {
                dial.Client(identity);
            }

            response.Append(dial);
            return TwiML(response);
        }

        if (!string.IsNullOrWhiteSpace(connection!.FallbackForwardNumber))
        {
            var dial = new Dial(callerId: to);
            dial.Number(new PhoneNumber(connection.FallbackForwardNumber));
            response.Append(dial);
            return TwiML(response);
        }

        response.Say("Nobody is available to take this call right now. Please try again later.");
        return TwiML(response);
    }

    private static async Task<IResult> HandleSmsAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        AppDbContext db,
        RealtimeNotifier notifier,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Webhooks.Sms");
        var (_, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        await RecordAndNotifyAsync(db, notifier, connectionId, new InboundEvent
        {
            ConnectionId = connectionId,
            Kind = "message",
            TwilioSid = form!["MessageSid"].ToString(),
            FromNumber = form["From"].ToString(),
            ToNumber = form["To"].ToString(),
            Body = form["Body"].ToString(),
            Status = "received",
            NumMedia = int.TryParse(form["NumMedia"].ToString(), out var n) ? n : 0,
        }, isCall: false);

        // An empty TwiML response means "delivered, do not auto-reply".
        return TwiML(new MessagingResponse());
    }

    private static async Task<IResult> HandleSmsStatusAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        AppDbContext db,
        RealtimeNotifier notifier,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Webhooks.SmsStatus");
        var (_, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        await notifier.MessageStatusAsync(
            connectionId, form!["MessageSid"].ToString(), form["MessageStatus"].ToString());
        return Results.Ok();
    }

    private static async Task<IResult> HandleVoiceStatusAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        AppDbContext db,
        RealtimeNotifier notifier,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Webhooks.VoiceStatus");
        var (_, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        await notifier.CallStatusAsync(
            connectionId, form!["CallSid"].ToString(), form["CallStatus"].ToString());
        return Results.Ok();
    }

    /// <summary>
    /// Verifies the caller really is Twilio. The webhook key proves the URL came from
    /// our provisioning step; the signature, when we hold an auth token, proves the
    /// request body was not tampered with in transit.
    /// </summary>
    private static async Task<(TwilioConnection?, IFormCollection?, IResult?)> AuthenticateAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        AppDbContext db,
        CredentialProtector protector,
        ILogger logger)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId);
        if (connection is null)
        {
            logger.LogWarning("Webhook for unknown connection {ConnectionId}", connectionId);
            return (null, null, Results.NotFound());
        }

        if (!CryptographicEquals(connection.WebhookKey, webhookKey))
        {
            logger.LogWarning("Webhook key mismatch for connection {ConnectionId}", connectionId);
            return (null, null, Results.Forbid());
        }

        if (!http.Request.HasFormContentType)
        {
            return (null, null, Results.BadRequest(new ApiError("expected_form_body")));
        }

        var form = await http.Request.ReadFormAsync();

        var authToken = protector.UnprotectOrNull(connection.AuthTokenCipher);
        if (!string.IsNullOrEmpty(authToken))
        {
            var signature = http.Request.Headers["X-Twilio-Signature"].ToString();
            var parameters = form.ToDictionary(f => f.Key, f => f.Value.ToString());
            var url = AbsoluteUrl(http);

            // Validate() throws rather than returning false on an absent signature,
            // which would turn an unsigned request into a 500 instead of a refusal.
            if (string.IsNullOrEmpty(signature) ||
                !new RequestValidator(authToken).Validate(url, parameters, signature))
            {
                logger.LogWarning(
                    "Rejected webhook for {ConnectionId}: bad X-Twilio-Signature on {Url}",
                    connectionId, url);
                return (null, null, Results.Forbid());
            }
        }

        return (connection, form, null);
    }

    /// <summary>
    /// Rebuilds the URL Twilio signed. Behind a proxy or TLS terminator the raw request
    /// scheme and host are the proxy's, so forwarded headers must already be applied.
    /// </summary>
    private static string AbsoluteUrl(HttpContext http)
    {
        var r = http.Request;
        return $"{r.Scheme}://{r.Host}{r.PathBase}{r.Path}{r.QueryString}";
    }

    private static async Task RecordAndNotifyAsync(
        AppDbContext db,
        RealtimeNotifier notifier,
        string connectionId,
        InboundEvent evt,
        bool isCall)
    {
        // Twilio retries webhooks, so a repeated SID is an expected duplicate.
        var exists = await db.InboundEvents.AnyAsync(e => e.TwilioSid == evt.TwilioSid);
        if (!exists)
        {
            db.InboundEvents.Add(evt);
            await db.SaveChangesAsync();
        }

        var dto = new InboundEventDto(
            evt.Id, evt.Kind, evt.TwilioSid, evt.FromNumber, evt.ToNumber,
            evt.Body, evt.Status, evt.NumMedia, evt.ReceivedAt);

        if (isCall)
        {
            await notifier.IncomingCallAsync(connectionId, dto);
        }
        else
        {
            await notifier.MessageReceivedAsync(connectionId, dto);
        }
    }

    private static string Value(IFormCollection form, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = form[key].ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// Serialises a TwiML document. Twilio's TwiML type has no parameterless ToString
    /// override, so calling ToString() on it yields the class name rather than XML.
    /// </summary>
    private static IResult TwiML(Twilio.TwiML.TwiML response) =>
        Results.Content(response.ToString(System.Xml.Linq.SaveOptions.None), "application/xml");
}

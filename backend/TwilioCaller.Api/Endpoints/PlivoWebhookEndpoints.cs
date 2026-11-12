using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Realtime;
using TwilioCaller.Api.Services;
using TwilioCaller.Api.Services.Plivo;

namespace TwilioCaller.Api.Endpoints;

/// <summary>
/// Endpoints Plivo calls: the answer, hangup and message URLs of the Application
/// this backend provisions. Same job as <see cref="WebhookEndpoints"/>, in Plivo's
/// dialect — its own parameter names, Plivo XML instead of TwiML, and a V3
/// signature that is always checked, because Plivo's auth token is always on file.
/// </summary>
public static class PlivoWebhookEndpoints
{
    /// <summary>The custom SIP header the app sends its chosen caller id in.</summary>
    public const string CallerIdHeader = "X-PH-CallerId";

    public static void MapPlivoWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks/{connectionId}/{webhookKey}/plivo")
            .AllowAnonymous()
            .WithTags("Webhooks (Plivo)");

        group.MapPost("/voice", HandleVoiceAsync);
        group.MapPost("/voice/status", HandleVoiceStatusAsync);
        group.MapPost("/sms", HandleSmsAsync);
        group.MapPost("/sms/status", HandleSmsStatusAsync);
        group.MapPost("/bridge/{to}/{callerId}", HandleBridgeAsync);
    }

    /// <summary>
    /// The Application's answer URL, hit for both call directions. A call placed from
    /// the Browser SDK arrives with the endpoint's SIP URI as From, which is how an
    /// app-originated call is told apart from someone dialling one of the numbers.
    /// </summary>
    private static async Task<IResult> HandleVoiceAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        AppDbContext db,
        ConnectionService connections,
        RealtimeNotifier notifier,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Webhooks.Plivo.Voice");
        var (connection, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        var from = form!["From"].ToString();
        var to = form["To"].ToString();
        var callUuid = form["CallUUID"].ToString();

        if (PlivoNumbers.IsSip(from))
        {
            // Outbound: the SDK dialled the destination and passed which of the
            // account's numbers to present in a custom SIP header.
            var destination = Digits(PlivoNumbers.SipUser(to));
            var callerId = Digits(Header(form, CallerIdHeader));

            if (destination.Length == 0 || callerId.Length == 0)
            {
                logger.LogWarning(
                    "Outbound Plivo call {CallUuid} missing destination or caller id", callUuid);
                return Xml(new XElement("Speak",
                    "The call could not be placed because it was missing a destination."));
            }

            return Xml(new XElement("Dial",
                new XAttribute("callerId", callerId),
                new XElement("Number", destination)));
        }

        var caller = PlivoNumbers.FromPlivo(from);
        var called = PlivoNumbers.FromPlivo(to);

        await WebhookEndpoints.RecordAndNotifyAsync(db, notifier, connectionId, new InboundEvent
        {
            ConnectionId = connectionId,
            Kind = "call",
            TwilioSid = callUuid,
            FromNumber = caller,
            ToNumber = called,
            Status = form["CallStatus"].ToString(),
        }, isCall: true);

        // Inbound: ring every browser that has signed in to the SDK.
        var endpoints = await connections.ActivePlivoEndpointsAsync(connectionId);
        if (endpoints.Count > 0)
        {
            return Xml(new XElement("Dial",
                new XAttribute("callerId", PlivoNumbers.ToPlivo(caller)),
                new XAttribute("timeout", 30),
                endpoints.Select(e => new XElement("User", PlivoNumbers.SipUri(e)))));
        }

        if (!string.IsNullOrWhiteSpace(connection!.FallbackForwardNumber))
        {
            return Xml(new XElement("Dial",
                new XAttribute("callerId", PlivoNumbers.ToPlivo(called)),
                new XElement("Number", PlivoNumbers.ToPlivo(connection.FallbackForwardNumber))));
        }

        return Xml(new XElement("Speak",
            "Nobody is available to take this call right now. Please try again later."));
    }

    /// <summary>
    /// Answer URL of a dial-out: the user's handset has picked up, so connect it to
    /// the number they wanted, presenting the number they chose.
    /// </summary>
    private static async Task<IResult> HandleBridgeAsync(
        HttpContext http,
        string connectionId,
        string webhookKey,
        string to,
        string callerId,
        AppDbContext db,
        CredentialProtector protector,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Webhooks.Plivo.Bridge");
        var (_, _, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        return Xml(
            new XElement("Speak", "Connecting you now."),
            new XElement("Dial",
                new XAttribute("callerId", Digits(callerId)),
                new XElement("Number", Digits(to))));
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
        var logger = loggerFactory.CreateLogger("Webhooks.Plivo.Sms");
        var (_, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        var uuid = form!["MessageUUID"].ToString();
        var from = PlivoNumbers.FromPlivo(form["From"].ToString());
        var to = PlivoNumbers.FromPlivo(form["To"].ToString());
        var text = form["Text"].ToString();
        // MMS attachments arrive as Media0, Media1, ...
        var media = form.Keys.Count(k =>
            k.StartsWith("Media", StringComparison.Ordinal) && k.Length > 5 && k[5..].All(char.IsAsciiDigit));

        // This copy is the only record of the text: Plivo will not serve it later.
        if (!await db.StoredMessages.AnyAsync(m => m.ConnectionId == connectionId && m.Sid == uuid))
        {
            db.StoredMessages.Add(new StoredMessage
            {
                ConnectionId = connectionId,
                Sid = uuid,
                FromNumber = from,
                ToNumber = to,
                Body = text,
                Direction = "inbound",
                Status = "received",
                NumMedia = media,
            });
            await db.SaveChangesAsync();
        }

        await WebhookEndpoints.RecordAndNotifyAsync(db, notifier, connectionId, new InboundEvent
        {
            ConnectionId = connectionId,
            Kind = "message",
            TwilioSid = uuid,
            FromNumber = from,
            ToNumber = to,
            Body = text,
            Status = "received",
            NumMedia = media,
        }, isCall: false);

        // An empty response means "received, nothing to send back".
        return Xml();
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
        var logger = loggerFactory.CreateLogger("Webhooks.Plivo.SmsStatus");
        var (_, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        var uuid = form!["MessageUUID"].ToString();
        var status = form["Status"].ToString();

        var stored = await db.StoredMessages
            .FirstOrDefaultAsync(m => m.ConnectionId == connectionId && m.Sid == uuid);
        if (stored is not null && !string.IsNullOrEmpty(status))
        {
            stored.Status = status;
            await db.SaveChangesAsync();
        }

        await notifier.MessageStatusAsync(connectionId, uuid, status);
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
        var logger = loggerFactory.CreateLogger("Webhooks.Plivo.VoiceStatus");
        var (_, form, failure) =
            await AuthenticateAsync(http, connectionId, webhookKey, db, protector, logger);
        if (failure is not null)
        {
            return failure;
        }

        await notifier.CallStatusAsync(
            connectionId, form!["CallUUID"].ToString(), form["CallStatus"].ToString());
        return Results.Ok();
    }

    /// <summary>
    /// Verifies the caller really is Plivo: the webhook key proves the URL came from
    /// our provisioning step, and the V3 signature proves the request was made by
    /// someone holding the account's auth token.
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
        if (connection is null || connection.Provider != Providers.Plivo)
        {
            logger.LogWarning("Plivo webhook for unknown connection {ConnectionId}", connectionId);
            return (null, null, Results.NotFound());
        }

        if (!WebhookEndpoints.CryptographicEquals(connection.WebhookKey, webhookKey))
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
        if (string.IsNullOrEmpty(authToken))
        {
            // Cannot happen for a connection made through /api/auth/connect; refuse
            // rather than serve an unverifiable request.
            return (null, null, Results.Forbid());
        }

        var url = WebhookEndpoints.AbsoluteUrl(http);
        var nonce = http.Request.Headers[PlivoSignature.NonceHeader].ToString();
        var parameters = form.Select(f => new KeyValuePair<string, IReadOnlyList<string>>(
            f.Key, f.Value.Select(v => v ?? string.Empty).ToList())).ToList();

        // A main account's token signs the "Ma" header; for a subaccount's resources
        // Plivo sends both, each signed with the respective token.
        var signed = new[] { PlivoSignature.SignatureHeader, PlivoSignature.MainAccountSignatureHeader }
            .Select(name => http.Request.Headers[name].ToString())
            .Any(signature => PlivoSignature.Validate(
                http.Request.Method, url, parameters, nonce, authToken, signature));

        // Messaging callbacks may arrive with the older V2 signature instead. It is
        // only honoured when no V3 signature was sent at all, so it can never be
        // used to slip past a V3 check that failed.
        var hasV3 = http.Request.Headers.ContainsKey(PlivoSignature.SignatureHeader) ||
                    http.Request.Headers.ContainsKey(PlivoSignature.MainAccountSignatureHeader);
        if (!signed && !hasV3)
        {
            signed = PlivoSignature.ValidateV2(
                url,
                http.Request.Headers[PlivoSignature.V2NonceHeader].ToString(),
                authToken,
                http.Request.Headers[PlivoSignature.V2SignatureHeader].ToString());
        }

        if (!signed)
        {
            logger.LogWarning(
                "Rejected Plivo webhook for {ConnectionId}: bad {Header} on {Url}",
                connectionId, PlivoSignature.SignatureHeader, url);
            return (null, null, Results.Forbid());
        }

        return (connection, form, null);
    }

    /// <summary>HTTP and SIP both treat header names case-insensitively, so look the same way.</summary>
    private static string Header(IFormCollection form, string name)
    {
        foreach (var (key, value) in form)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>Plivo dials bare digits; this also keeps anything odd out of the XML.</summary>
    private static string Digits(string value) =>
        new(value.Where(char.IsAsciiDigit).ToArray());

    private static IResult Xml(params XElement[] verbs) =>
        Results.Content(
            new XElement("Response", verbs).ToString(SaveOptions.DisableFormatting),
            "application/xml");
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services;

namespace TwilioCaller.Api.Endpoints;

/// <summary>
/// Everything the Flutter app and the portal call once signed in. All of it acts on
/// the calling user's own Twilio connection; account management lives in
/// <see cref="AdminEndpoints"/>.
/// </summary>
public static class ApiEndpoints
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/twilio/connect", ConnectTwilioAsync).WithTags("Twilio");
        api.MapDelete("/twilio/connection", DisconnectTwilioAsync).WithTags("Twilio");
        api.MapGet("/numbers", ListNumbersAsync).WithTags("Numbers");
        api.MapPost("/numbers/provision", ProvisionAsync).WithTags("Numbers");
        api.MapGet("/messages", ListMessagesAsync).WithTags("Messages");
        api.MapGet("/messages/conversations", ListConversationsAsync).WithTags("Messages");
        api.MapPost("/messages", SendMessageAsync).WithTags("Messages");
        api.MapGet("/calls", ListCallsAsync).WithTags("Calls");
        api.MapPost("/calls/dial-out", DialOutAsync).WithTags("Calls");
        api.MapGet("/voice/token", VoiceTokenAsync).WithTags("Calls");
        api.MapGet("/events", ListEventsAsync).WithTags("Events");
    }

    private static async Task<IResult> ConnectTwilioAsync(
        TwilioConnectRequest request, ClaimsPrincipal user, ConnectionService connections,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AccountSid) ||
            string.IsNullOrWhiteSpace(request.ApiKeySid) ||
            string.IsNullOrWhiteSpace(request.ApiKeySecret))
        {
            return Results.BadRequest(
                new ApiError("missing_credentials",
                    "accountSid, apiKeySid and apiKeySecret are all required."));
        }

        if (!request.AccountSid.StartsWith("AC", StringComparison.Ordinal))
        {
            return Results.BadRequest(
                new ApiError("bad_account_sid", "A Twilio Account SID starts with 'AC'."));
        }

        if (!request.ApiKeySid.StartsWith("SK", StringComparison.Ordinal))
        {
            return Results.BadRequest(
                new ApiError("bad_api_key_sid",
                    "A Twilio API Key SID starts with 'SK'. The value starting with 'AC' is the " +
                    "Account SID, not an API key."));
        }

        var (userId, _) = AuthEndpoints.Caller(user);

        try
        {
            var connection = await connections.ConnectAsync(userId, request, ct);
            return Results.Ok(AuthEndpoints.TwilioConnectionDtoOf(connection));
        }
        catch (TwilioCredentialException ex)
        {
            return Results.Json(
                new ApiError("twilio_rejected_credentials", ex.Message), statusCode: 401);
        }
    }

    private static async Task<IResult> DisconnectTwilioAsync(
        ClaimsPrincipal user, ConnectionService connections, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Caller(user);
        return await connections.DisconnectAsync(userId, ct)
            ? Results.Ok()
            : NoTwilioConnection();
    }

    private static Task<IResult> ListNumbersAsync(
        ClaimsPrincipal user, ConnectionService connections, TwilioApiService twilio,
        CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await twilio.ListNumbersAsync(c, ct)));

    private static Task<IResult> ProvisionAsync(
        ProvisionRequest request, ClaimsPrincipal user, ConnectionService connections,
        ProvisioningService provisioning, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await provisioning.ProvisionNumbersAsync(c, request, ct)));

    private static Task<IResult> ListMessagesAsync(
        string number, string? peer, int? limit, ClaimsPrincipal user,
        ConnectionService connections, TwilioApiService twilio, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await twilio.ListMessagesAsync(c, number, peer, Clamp(limit), ct)));

    private static Task<IResult> ListConversationsAsync(
        string number, int? limit, ClaimsPrincipal user, ConnectionService connections,
        TwilioApiService twilio, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await twilio.ListConversationsAsync(c, number, Clamp(limit, 200), ct)));

    private static Task<IResult> SendMessageAsync(
        SendMessageRequest request, ClaimsPrincipal user, ConnectionService connections,
        TwilioApiService twilio, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
        {
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new ApiError("empty_body", "A message body is required."));
            }

            return Results.Ok(await twilio.SendMessageAsync(c, request, ct));
        });

    private static Task<IResult> ListCallsAsync(
        string? number, int? limit, ClaimsPrincipal user, ConnectionService connections,
        TwilioApiService twilio, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await twilio.ListCallsAsync(c, number, Clamp(limit), ct)));

    private static Task<IResult> DialOutAsync(
        DialOutRequest request, ClaimsPrincipal user, ConnectionService connections,
        TwilioApiService twilio, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
        {
            var bridgeTo = string.IsNullOrWhiteSpace(request.BridgeTo)
                ? c.FallbackForwardNumber
                : request.BridgeTo;

            if (string.IsNullOrWhiteSpace(bridgeTo))
            {
                return Results.BadRequest(new ApiError(
                    "no_bridge_number",
                    "Dial-out rings your own handset first, so it needs a number to ring. " +
                    "Set a fallback forward number or pass bridgeTo."));
            }

            return Results.Ok(await twilio.DialOutAsync(c, request with { BridgeTo = bridgeTo }, ct));
        });

    private static Task<IResult> VoiceTokenAsync(
        ClaimsPrincipal user, ConnectionService connections, VoiceTokenService voice,
        ProvisioningService provisioning, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
        {
            var (userId, identity) = AuthEndpoints.Caller(user);

            if (string.IsNullOrEmpty(c.TwimlAppSid))
            {
                await provisioning.EnsureTwimlAppAsync(c, ct);
            }

            var platform = await connections.PlatformOfAsync(userId, identity, ct);
            var (token, expiresAt) = voice.Issue(c, identity, platform);
            await connections.TouchDeviceAsync(userId, identity, ct);
            return Results.Ok(new VoiceTokenResponse(token, identity, expiresAt));
        });

    /// <summary>
    /// Replays inbound events the app may have missed while it was backgrounded or
    /// offline. The realtime hub only delivers to clients that are actually connected.
    /// </summary>
    private static Task<IResult> ListEventsAsync(
        long? since, int? limit, ClaimsPrincipal user, ConnectionService connections,
        AppDbContext db, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
        {
            var events = await db.InboundEvents
                .Where(e => e.ConnectionId == c.Id && e.Id > (since ?? 0))
                .OrderBy(e => e.Id)
                .Take(Clamp(limit, 200))
                .Select(e => new InboundEventDto(
                    e.Id, e.Kind, e.TwilioSid, e.FromNumber, e.ToNumber,
                    e.Body, e.Status, e.NumMedia, e.ReceivedAt))
                .ToListAsync(ct);

            return Results.Ok(events);
        });

    private static async Task<IResult> WithConnection(
        ClaimsPrincipal user,
        ConnectionService connections,
        CancellationToken ct,
        Func<TwilioConnection, Task<IResult>> action)
    {
        var (userId, _) = AuthEndpoints.Caller(user);
        var connection = await connections.FindByUserAsync(userId, ct);
        if (connection is null)
        {
            return NoTwilioConnection();
        }

        try
        {
            return await action(connection);
        }
        catch (Twilio.Exceptions.ApiException ex)
        {
            // Surface Twilio's own message; it is far more actionable than a 500.
            return Results.Json(
                new ApiError("twilio_error", ex.Message),
                statusCode: ex.Status >= 400 && ex.Status < 600 ? ex.Status : 502);
        }
    }

    private static IResult NoTwilioConnection() => Results.Json(
        new ApiError("no_twilio_connection",
            "This account has no Twilio account connected yet."),
        statusCode: 409);

    private static int Clamp(int? value, int max = 100) => Math.Clamp(value ?? 50, 1, max);
}

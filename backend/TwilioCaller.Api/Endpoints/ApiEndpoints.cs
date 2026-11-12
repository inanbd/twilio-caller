using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services;
using TwilioCaller.Api.Services.Plivo;

namespace TwilioCaller.Api.Endpoints;

/// <summary>Everything the Flutter app calls, all behind a session bearer token.</summary>
public static class ApiEndpoints
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/connect", ConnectAsync)
            .AllowAnonymous()
            .WithTags("Auth");

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/auth/session", GetSessionAsync).WithTags("Auth");
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

    private static async Task<IResult> ConnectAsync(
        ConnectRequest request, ConnectionService connections, CancellationToken ct)
    {
        var provider = Providers.Normalise(request.Provider);
        if (provider is null)
        {
            return Results.BadRequest(
                new ApiError("unknown_provider", "provider must be 'twilio' or 'plivo'."));
        }

        if (provider == Providers.Plivo)
        {
            return await ConnectPlivoAsync(request, connections, ct);
        }

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

        try
        {
            return Results.Ok(await connections.ConnectAsync(request, ct));
        }
        catch (TwilioCredentialException ex)
        {
            return Results.Json(
                new ApiError("twilio_rejected_credentials", ex.Message), statusCode: 401);
        }
    }

    private static async Task<IResult> ConnectPlivoAsync(
        ConnectRequest request, ConnectionService connections, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AccountSid) ||
            string.IsNullOrWhiteSpace(request.AuthToken))
        {
            return Results.BadRequest(
                new ApiError("missing_credentials",
                    "For Plivo, accountSid (your Auth ID) and authToken are both required."));
        }

        var authId = request.AccountSid.Trim();
        if (!authId.StartsWith("MA", StringComparison.Ordinal) &&
            !authId.StartsWith("SA", StringComparison.Ordinal))
        {
            return Results.BadRequest(
                new ApiError("bad_auth_id",
                    "A Plivo Auth ID starts with 'MA' (or 'SA' for a subaccount)."));
        }

        try
        {
            return Results.Ok(await connections.ConnectAsync(request, ct));
        }
        catch (PlivoCredentialException ex)
        {
            return Results.Json(
                new ApiError("plivo_rejected_credentials", ex.Message), statusCode: 401);
        }
    }

    private static async Task<IResult> GetSessionAsync(
        ClaimsPrincipal user, AppDbContext db, ConnectionService connections, CancellationToken ct)
    {
        var (connectionId, identity) = Caller(user);
        var connection = await connections.FindAsync(connectionId, ct);
        if (connection is null)
        {
            return Results.Unauthorized();
        }

        await connections.TouchDeviceAsync(connectionId, identity, ct);

        return Results.Ok(new SessionResponse(
            connection.Id,
            connection.AccountSid,
            connection.FriendlyName,
            identity,
            VoiceReady: !string.IsNullOrEmpty(connection.TwimlAppSid),
            connection.TwimlAppSid,
            connection.FallbackForwardNumber,
            connection.Provider));
    }

    private static Task<IResult> ListNumbersAsync(
        ClaimsPrincipal user, ConnectionService connections, ProviderRouter providers,
        CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await providers.Api(c).ListNumbersAsync(c, ct)));

    private static Task<IResult> ProvisionAsync(
        ProvisionRequest request, ClaimsPrincipal user, ConnectionService connections,
        ProviderRouter providers, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await providers.Provisioner(c).ProvisionNumbersAsync(c, request, ct)));

    private static Task<IResult> ListMessagesAsync(
        string number, string? peer, int? limit, ClaimsPrincipal user,
        ConnectionService connections, ProviderRouter providers, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await providers.Api(c).ListMessagesAsync(c, number, peer, Clamp(limit), ct)));

    private static Task<IResult> ListConversationsAsync(
        string number, int? limit, ClaimsPrincipal user, ConnectionService connections,
        ProviderRouter providers, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await providers.Api(c).ListConversationsAsync(c, number, Clamp(limit, 200), ct)));

    private static Task<IResult> SendMessageAsync(
        SendMessageRequest request, ClaimsPrincipal user, ConnectionService connections,
        ProviderRouter providers, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
        {
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new ApiError("empty_body", "A message body is required."));
            }

            return Results.Ok(await providers.Api(c).SendMessageAsync(c, request, ct));
        });

    private static Task<IResult> ListCallsAsync(
        string? number, int? limit, ClaimsPrincipal user, ConnectionService connections,
        ProviderRouter providers, CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
            Results.Ok(await providers.Api(c).ListCallsAsync(c, number, Clamp(limit), ct)));

    private static Task<IResult> DialOutAsync(
        DialOutRequest request, ClaimsPrincipal user, ConnectionService connections,
        ProviderRouter providers, CancellationToken ct) =>
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

            return Results.Ok(await providers.Api(c).DialOutAsync(c, request with { BridgeTo = bridgeTo }, ct));
        });

    private static Task<IResult> VoiceTokenAsync(
        ClaimsPrincipal user, ConnectionService connections, VoiceTokenService voice,
        ProvisioningService provisioning, PlivoProvisioningService plivoProvisioning,
        CancellationToken ct) =>
        WithConnection(user, connections, ct, async c =>
        {
            var (_, identity) = Caller(user);

            if (c.Provider == Providers.Plivo)
            {
                // Plivo's in-app audio is a browser SIP endpoint. A device that said
                // it cannot run one must not be handed credentials for it.
                if (!await connections.SupportsVoipAsync(c.Id, identity, ct))
                {
                    return Results.BadRequest(new ApiError(
                        "voip_unsupported",
                        "In-app calling with Plivo is only available in the web app. " +
                        "Use dial-out on this device."));
                }

                var (username, password) = await plivoProvisioning.EnsureEndpointAsync(c, identity, ct);
                await connections.TouchDeviceAsync(c.Id, identity, ct);

                // Endpoint credentials do not expire; the far-off date just keeps the
                // app's refresh timer quiet.
                return Results.Ok(new VoiceTokenResponse(
                    Token: string.Empty,
                    Identity: identity,
                    ExpiresAt: DateTimeOffset.UtcNow.AddYears(1),
                    Provider: Providers.Plivo,
                    Username: username,
                    Password: password));
            }

            if (string.IsNullOrEmpty(c.TwimlAppSid))
            {
                await provisioning.EnsureTwimlAppAsync(c, ct);
            }

            var platform = await connections.PlatformOfAsync(c.Id, identity, ct);
            var (token, expiresAt) = voice.Issue(c, identity, platform);
            await connections.TouchDeviceAsync(c.Id, identity, ct);
            return Results.Ok(new VoiceTokenResponse(token, identity, expiresAt));
        });

    /// <summary>
    /// Replays inbound events the app may have missed while it was backgrounded or
    /// offline. The realtime hub only delivers to clients that are actually connected.
    /// </summary>
    private static async Task<IResult> ListEventsAsync(
        long? since, int? limit, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var (connectionId, _) = Caller(user);

        var events = await db.InboundEvents
            .Where(e => e.ConnectionId == connectionId && e.Id > (since ?? 0))
            .OrderBy(e => e.Id)
            .Take(Clamp(limit, 200))
            .Select(e => new InboundEventDto(
                e.Id, e.Kind, e.TwilioSid, e.FromNumber, e.ToNumber,
                e.Body, e.Status, e.NumMedia, e.ReceivedAt))
            .ToListAsync(ct);

        return Results.Ok(events);
    }

    private static async Task<IResult> WithConnection(
        ClaimsPrincipal user,
        ConnectionService connections,
        CancellationToken ct,
        Func<TwilioConnection, Task<IResult>> action)
    {
        var (connectionId, _) = Caller(user);
        var connection = await connections.FindAsync(connectionId, ct);
        if (connection is null)
        {
            return Results.Unauthorized();
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
        catch (PlivoApiException ex)
        {
            // A 401 from Plivo must not look like an expired app session, which is
            // what the app takes a 401 from this backend to mean.
            var status = ex.Status is >= 400 and < 600 and not 401 ? ex.Status : 502;
            return Results.Json(new ApiError("plivo_error", ex.Message), statusCode: status);
        }
    }

    private static (string ConnectionId, string Identity) Caller(ClaimsPrincipal user) => (
        user.FindFirst(SessionTokenService.ConnectionIdClaim)?.Value ?? string.Empty,
        user.FindFirst(SessionTokenService.IdentityClaim)?.Value ?? string.Empty);

    private static int Clamp(int? value, int max = 100) => Math.Clamp(value ?? 50, 1, max);
}

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services.Plivo;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Owns the login flow. The app collects the provider credentials once (a Twilio API
/// key and secret, or a Plivo Auth ID and token); from then on it holds only our own
/// session token, and the provider secret lives encrypted on the server.
/// </summary>
public class ConnectionService(
    AppDbContext db,
    CredentialProtector protector,
    TwilioApiService twilio,
    PlivoApiService plivo,
    SessionTokenService sessions,
    ProviderRouter providers,
    ILogger<ConnectionService> logger)
{
    public async Task<ConnectResponse> ConnectAsync(ConnectRequest request, CancellationToken ct = default)
    {
        var provider = Providers.Normalise(request.Provider)
            ?? throw new ArgumentException($"Unknown provider '{request.Provider}'.");
        var accountSid = request.AccountSid.Trim();

        var friendlyName = provider == Providers.Plivo
            ? await plivo.ValidateCredentialsAsync(accountSid, request.AuthToken!.Trim(), ct)
            : await twilio.ValidateCredentialsAsync(
                accountSid, request.ApiKeySid!, request.ApiKeySecret!, ct);

        // Re-use the row for an account we have already seen so that a re-login keeps
        // the same webhook URLs and voice application rather than orphaning them.
        var connection = await db.Connections
            .FirstOrDefaultAsync(c => c.AccountSid == accountSid && c.Provider == provider, ct);

        if (connection is null)
        {
            connection = new TwilioConnection
            {
                Provider = provider,
                AccountSid = accountSid,
                WebhookKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            };
            db.Connections.Add(connection);
        }

        if (provider == Providers.Plivo)
        {
            connection.AuthTokenCipher = protector.Protect(request.AuthToken!.Trim());
        }
        else
        {
            connection.ApiKeySid = request.ApiKeySid!;
            connection.ApiKeySecretCipher = protector.Protect(request.ApiKeySecret!);
            connection.AuthTokenCipher = string.IsNullOrWhiteSpace(request.AuthToken)
                ? connection.AuthTokenCipher
                : protector.Protect(request.AuthToken);
        }

        connection.FriendlyName = friendlyName;
        connection.LastSeenAt = DateTimeOffset.UtcNow;

        var identity = SessionTokenService.NewDeviceIdentity(request.Platform ?? "device");
        db.Devices.Add(new DeviceRegistration
        {
            ConnectionId = connection.Id,
            Identity = identity,
            Platform = request.Platform ?? "unknown",
            SupportsVoip = request.SupportsVoip,
        });

        await db.SaveChangesAsync(ct);

        // Best effort: a missing voice application only disables in-app calling
        // until numbers are provisioned; it must not block login.
        try
        {
            await providers.Provisioner(connection).EnsureAppAsync(connection, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not provision the voice application during connect for {Account}",
                accountSid);
        }

        var (token, expiresAt) = sessions.Issue(connection.Id, identity);

        return new ConnectResponse(
            SessionToken: token,
            ExpiresAt: expiresAt,
            ConnectionId: connection.Id,
            AccountSid: connection.AccountSid,
            FriendlyName: connection.FriendlyName,
            Identity: identity,
            VoiceReady: !string.IsNullOrEmpty(connection.TwimlAppSid),
            Provider: connection.Provider);
    }

    public Task<TwilioConnection?> FindAsync(string connectionId, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);

    /// <summary>
    /// Resolves the Voice client identities that should ring for an inbound call.
    /// Devices that cannot run the Voice SDK are excluded so we do not dial a client
    /// that will never register.
    /// </summary>
    public async Task<IReadOnlyList<string>> ActiveVoipIdentitiesAsync(
        string connectionId, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        return await db.Devices
            .Where(d => d.ConnectionId == connectionId && d.SupportsVoip && d.LastSeenAt >= cutoff)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => d.Identity)
            .Take(5)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The Plivo flavour of <see cref="ActiveVoipIdentitiesAsync"/>: the SIP endpoints
    /// of devices that have brought the Browser SDK up at least once.
    /// </summary>
    public async Task<IReadOnlyList<string>> ActivePlivoEndpointsAsync(
        string connectionId, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        return await db.Devices
            .Where(d => d.ConnectionId == connectionId && d.SupportsVoip &&
                        d.PlivoEndpointUsername != null && d.LastSeenAt >= cutoff)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => d.PlivoEndpointUsername!)
            .Take(5)
            .ToListAsync(ct);
    }

    public Task<bool> SupportsVoipAsync(
        string connectionId, string identity, CancellationToken ct = default) =>
        db.Devices.AnyAsync(
            d => d.ConnectionId == connectionId && d.Identity == identity && d.SupportsVoip, ct);

    public Task<string?> PlatformOfAsync(
        string connectionId, string identity, CancellationToken ct = default) =>
        db.Devices
            .Where(d => d.ConnectionId == connectionId && d.Identity == identity)
            .Select(d => (string?)d.Platform)
            .FirstOrDefaultAsync(ct);

    public async Task TouchDeviceAsync(string connectionId, string identity, CancellationToken ct = default)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.ConnectionId == connectionId && d.Identity == identity, ct);
        if (device is null)
        {
            return;
        }

        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

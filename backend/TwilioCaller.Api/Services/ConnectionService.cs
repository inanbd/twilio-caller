using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Owns the login flow. The app collects the Twilio API key and secret once; from
/// then on it holds only our own session token, and the Twilio secret lives encrypted
/// on the server.
/// </summary>
public class ConnectionService(
    AppDbContext db,
    CredentialProtector protector,
    TwilioApiService twilio,
    SessionTokenService sessions,
    ProvisioningService provisioning,
    ILogger<ConnectionService> logger)
{
    public async Task<ConnectResponse> ConnectAsync(ConnectRequest request, CancellationToken ct = default)
    {
        var friendlyName = await twilio.ValidateCredentialsAsync(
            request.AccountSid, request.ApiKeySid, request.ApiKeySecret, ct);

        // Re-use the row for an account we have already seen so that a re-login keeps
        // the same webhook URLs and TwiML app rather than orphaning them on Twilio.
        var connection = await db.Connections
            .FirstOrDefaultAsync(c => c.AccountSid == request.AccountSid, ct);

        if (connection is null)
        {
            connection = new TwilioConnection
            {
                AccountSid = request.AccountSid,
                WebhookKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            };
            db.Connections.Add(connection);
        }

        connection.ApiKeySid = request.ApiKeySid;
        connection.ApiKeySecretCipher = protector.Protect(request.ApiKeySecret);
        connection.AuthTokenCipher = string.IsNullOrWhiteSpace(request.AuthToken)
            ? connection.AuthTokenCipher
            : protector.Protect(request.AuthToken);
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

        // Best effort: a missing TwiML app only disables VoIP, it must not block login.
        try
        {
            await provisioning.EnsureTwimlAppAsync(connection, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not provision TwiML app during connect for {Account}",
                request.AccountSid);
        }

        var (token, expiresAt) = sessions.Issue(connection.Id, identity);

        return new ConnectResponse(
            SessionToken: token,
            ExpiresAt: expiresAt,
            ConnectionId: connection.Id,
            AccountSid: connection.AccountSid,
            FriendlyName: connection.FriendlyName,
            Identity: identity,
            VoiceReady: !string.IsNullOrEmpty(connection.TwimlAppSid));
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

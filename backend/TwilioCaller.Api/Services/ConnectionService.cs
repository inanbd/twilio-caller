using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Owns each user's Twilio connection and devices. The user hands over the Twilio
/// API key once, after logging in; from then on the secret lives encrypted on the
/// server and the account is managed through their user login.
/// </summary>
public class ConnectionService(
    AppDbContext db,
    CredentialProtector protector,
    TwilioApiService twilio,
    ProvisioningService provisioning,
    ILogger<ConnectionService> logger)
{
    /// <summary>Attaches a Twilio account to the user, or refreshes the one they have.</summary>
    public async Task<TwilioConnection> ConnectAsync(
        string userId, TwilioConnectRequest request, CancellationToken ct = default)
    {
        var friendlyName = await twilio.ValidateCredentialsAsync(
            request.AccountSid, request.ApiKeySid, request.ApiKeySecret, ct);

        // Re-use the user's existing row so that a re-connect keeps the same webhook
        // URLs and TwiML app rather than orphaning them on Twilio.
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.UserId == userId, ct);

        if (connection is null)
        {
            connection = new TwilioConnection
            {
                UserId = userId,
                WebhookKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            };
            db.Connections.Add(connection);
        }
        else if (connection.AccountSid != request.AccountSid)
        {
            // A different Twilio account gets fresh webhook URLs; the old account's
            // numbers keep pointing at a key that no longer authenticates.
            connection.WebhookKey =
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            connection.TwimlAppSid = null;
        }

        connection.AccountSid = request.AccountSid;
        connection.ApiKeySid = request.ApiKeySid;
        connection.ApiKeySecretCipher = protector.Protect(request.ApiKeySecret);
        connection.AuthTokenCipher = string.IsNullOrWhiteSpace(request.AuthToken)
            ? connection.AuthTokenCipher
            : protector.Protect(request.AuthToken);
        connection.FriendlyName = friendlyName;
        connection.LastSeenAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        // Best effort: a missing TwiML app only disables VoIP, it must not block connect.
        try
        {
            await provisioning.EnsureTwimlAppAsync(connection, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not provision TwiML app during connect for {Account}",
                request.AccountSid);
        }

        return connection;
    }

    /// <summary>Removes the user's Twilio connection and the events captured for it.</summary>
    public async Task<bool> DisconnectAsync(string userId, CancellationToken ct = default)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (connection is null)
        {
            return false;
        }

        await db.InboundEvents.Where(e => e.ConnectionId == connection.Id).ExecuteDeleteAsync(ct);
        db.Connections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<TwilioConnection?> FindByUserAsync(string userId, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(c => c.UserId == userId, ct);

    public Task<TwilioConnection?> FindAsync(string connectionId, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);

    /// <summary>Registers the logging-in device and mints its Twilio Voice identity.</summary>
    public async Task<DeviceRegistration> RegisterDeviceAsync(
        string userId, string? platform, bool supportsVoip, CancellationToken ct = default)
    {
        var device = new DeviceRegistration
        {
            UserId = userId,
            Identity = SessionTokenService.NewDeviceIdentity(platform ?? "device"),
            Platform = platform ?? "unknown",
            SupportsVoip = supportsVoip,
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return device;
    }

    /// <summary>
    /// Resolves the Voice client identities that should ring for an inbound call to
    /// this connection: the owning user's devices. Devices that cannot run the Voice
    /// SDK are excluded so we do not dial a client that will never register.
    /// </summary>
    public async Task<IReadOnlyList<string>> ActiveVoipIdentitiesAsync(
        string connectionId, CancellationToken ct = default)
    {
        var userId = await db.Connections
            .Where(c => c.Id == connectionId)
            .Select(c => c.UserId)
            .FirstOrDefaultAsync(ct);
        if (userId is null)
        {
            return Array.Empty<string>();
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        return await db.Devices
            .Where(d => d.UserId == userId && d.SupportsVoip && d.LastSeenAt >= cutoff)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => d.Identity)
            .Take(5)
            .ToListAsync(ct);
    }

    public Task<string?> PlatformOfAsync(
        string userId, string identity, CancellationToken ct = default) =>
        db.Devices
            .Where(d => d.UserId == userId && d.Identity == identity)
            .Select(d => (string?)d.Platform)
            .FirstOrDefaultAsync(ct);

    public async Task TouchDeviceAsync(string userId, string identity, CancellationToken ct = default)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.UserId == userId && d.Identity == identity, ct);
        if (device is null)
        {
            return;
        }

        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

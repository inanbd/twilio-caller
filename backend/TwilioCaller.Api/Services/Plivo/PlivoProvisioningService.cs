using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services.Plivo;

/// <summary>
/// Points Plivo at this backend. Plivo hangs everything off an Application: it
/// holds the answer, hangup and message URLs, and numbers and SIP endpoints are
/// attached to it. So provisioning is "one application, then attach things".
/// </summary>
public class PlivoProvisioningService(
    AppDbContext db,
    PlivoClient plivo,
    CredentialProtector protector,
    WebhookUrlBuilder urls,
    ILogger<PlivoProvisioningService> logger) : INumberProvisioner
{
    /// <summary>
    /// Creates the Application if this connection has none, or refreshes its URLs if
    /// it already exists. Idempotent: safe to call on every login.
    /// </summary>
    public async Task<string> EnsureAppAsync(TwilioConnection connection, CancellationToken ct = default)
    {
        var settings = new Dictionary<string, object>
        {
            ["answer_url"] = urls.Voice(connection).ToString(),
            ["answer_method"] = "POST",
            ["hangup_url"] = urls.VoiceStatus(connection).ToString(),
            ["hangup_method"] = "POST",
            ["message_url"] = urls.Sms(connection).ToString(),
            ["message_method"] = "POST",
        };

        if (!string.IsNullOrEmpty(connection.TwimlAppSid))
        {
            try
            {
                await plivo.PostAsync(connection, $"Application/{connection.TwimlAppSid}/", settings, ct);
                return connection.TwimlAppSid;
            }
            catch (PlivoApiException ex) when (ex.Status == 404)
            {
                logger.LogWarning(
                    "Plivo application {AppId} no longer exists on account {Account}; recreating.",
                    connection.TwimlAppSid, connection.AccountSid);

                // Endpoints die with the application they were attached to.
                var orphaned = await db.Devices
                    .Where(d => d.ConnectionId == connection.Id && d.PlivoEndpointId != null)
                    .ToListAsync(ct);
                foreach (var device in orphaned)
                {
                    ClearEndpoint(device);
                }
            }
        }

        // Plivo only accepts letters, digits, '-' and '_' in an application name.
        settings["app_name"] = $"caller_flutter_{connection.Id[..Math.Min(8, connection.Id.Length)]}";
        var created = await plivo.PostAsync(connection, "Application/", settings, ct);

        connection.TwimlAppSid = created.Str("app_id")
            ?? throw new PlivoApiException(502, "Plivo created an application but returned no app_id.");
        await db.SaveChangesAsync(ct);
        return connection.TwimlAppSid;
    }

    /// <summary>
    /// Attaches the chosen numbers to our application so their inbound calls and
    /// messages land here. Numbers the user did not choose are left untouched.
    /// </summary>
    public async Task<ProvisionResponse> ProvisionNumbersAsync(
        TwilioConnection connection, ProvisionRequest request, CancellationToken ct = default)
    {
        var appId = await EnsureAppAsync(connection, ct);

        var updated = new List<string>();
        var failures = new List<string>();

        foreach (var number in request.PhoneNumberSids.Distinct())
        {
            var digits = PlivoNumbers.ToPlivo(number);
            try
            {
                await plivo.PostAsync(
                    connection, $"Number/{Uri.EscapeDataString(digits)}/", new { app_id = appId }, ct);
                updated.Add(number);
            }
            catch (PlivoApiException ex)
            {
                logger.LogWarning(ex, "Could not provision Plivo number {Number}", digits);
                failures.Add($"{number}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.FallbackForwardNumber))
        {
            connection.FallbackForwardNumber = request.FallbackForwardNumber;
            await db.SaveChangesAsync(ct);
        }

        return new ProvisionResponse(appId, updated, failures);
    }

    /// <summary>
    /// Returns the SIP endpoint a device signs in to the Browser SDK with, creating
    /// it on first use. One endpoint per device, so an inbound call can ring each
    /// open browser and a sign-in on one never knocks another offline.
    /// </summary>
    public async Task<(string Username, string Password)> EnsureEndpointAsync(
        TwilioConnection connection, string identity, CancellationToken ct = default)
    {
        var appId = await EnsureAppAsync(connection, ct);

        var device = await db.Devices.FirstOrDefaultAsync(
                         d => d.ConnectionId == connection.Id && d.Identity == identity, ct)
                     ?? throw new InvalidOperationException($"Unknown device {identity}.");

        var password = protector.UnprotectOrNull(device.PlivoEndpointPasswordCipher);
        if (!string.IsNullOrEmpty(device.PlivoEndpointUsername) && !string.IsNullOrEmpty(password))
        {
            return (device.PlivoEndpointUsername, password);
        }

        await RetireStaleEndpointsAsync(connection, ct);

        password = RandomToken(24);
        var created = await plivo.PostAsync(connection, "Endpoint/", new
        {
            // Plivo appends digits of its own to make the username unique, so the
            // one that matters is the one it answers with.
            username = $"caller{RandomToken(6)}",
            password,
            alias = new string(identity.Where(char.IsAsciiLetterOrDigit).ToArray()),
            app_id = appId,
        }, ct);

        device.PlivoEndpointUsername = created.Str("username")
            ?? throw new PlivoApiException(502, "Plivo created an endpoint but returned no username.");
        device.PlivoEndpointId = created.Str("endpoint_id");
        device.PlivoEndpointPasswordCipher = protector.Protect(password);
        await db.SaveChangesAsync(ct);

        return (device.PlivoEndpointUsername, password);
    }

    /// <summary>
    /// Every sign-in registers a new device, and unlike a Twilio client identity a
    /// Plivo endpoint is a real resource on the account. Remove the ones whose
    /// devices have not been seen for a month so they do not pile up.
    /// </summary>
    private async Task RetireStaleEndpointsAsync(TwilioConnection connection, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var stale = await db.Devices
            .Where(d => d.ConnectionId == connection.Id &&
                        d.PlivoEndpointId != null &&
                        d.LastSeenAt < cutoff)
            .ToListAsync(ct);

        foreach (var device in stale)
        {
            try
            {
                await plivo.DeleteAsync(connection, $"Endpoint/{device.PlivoEndpointId}/", ct);
            }
            catch (PlivoApiException ex)
            {
                logger.LogInformation(
                    "Could not delete stale Plivo endpoint {EndpointId}: {Message}",
                    device.PlivoEndpointId, ex.Message);
            }

            ClearEndpoint(device);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static void ClearEndpoint(DeviceRegistration device)
    {
        device.PlivoEndpointId = null;
        device.PlivoEndpointUsername = null;
        device.PlivoEndpointPasswordCipher = null;
    }

    private static string RandomToken(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }
}

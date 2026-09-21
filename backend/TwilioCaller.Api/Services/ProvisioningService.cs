using Microsoft.EntityFrameworkCore;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Points Twilio at this backend. Without this the app cannot receive anything:
/// inbound calls and SMS only reach us if the number's VoiceUrl/SmsUrl name our
/// webhooks, and outbound VoIP only works through a TwiML Application whose voice
/// URL is ours. Doing it here means the user never has to touch the Twilio console.
/// </summary>
public class ProvisioningService(
    AppDbContext db,
    TwilioClientFactory clientFactory,
    WebhookUrlBuilder urls,
    ILogger<ProvisioningService> logger)
{
    private const string TwimlAppFriendlyName = "Twilio Caller (Flutter)";

    /// <summary>
    /// Creates the TwiML Application if this connection has none, or refreshes its
    /// URLs if it already exists. Idempotent: safe to call on every login.
    /// </summary>
    public async Task<string> EnsureTwimlAppAsync(TwilioConnection connection, CancellationToken ct = default)
    {
        var client = clientFactory.Create(connection);
        var voiceUrl = urls.Voice(connection);
        var statusUrl = urls.VoiceStatus(connection);

        if (!string.IsNullOrEmpty(connection.TwimlAppSid))
        {
            try
            {
                await ApplicationResource.UpdateAsync(
                    pathSid: connection.TwimlAppSid,
                    voiceUrl: voiceUrl,
                    voiceMethod: Twilio.Http.HttpMethod.Post,
                    statusCallback: statusUrl,
                    statusCallbackMethod: Twilio.Http.HttpMethod.Post,
                    client: client);
                return connection.TwimlAppSid;
            }
            catch (ApiException ex) when (ex.Status == 404)
            {
                logger.LogWarning(
                    "TwiML app {Sid} no longer exists on account {Account}; recreating.",
                    connection.TwimlAppSid, connection.AccountSid);
            }
        }

        var app = await ApplicationResource.CreateAsync(
            friendlyName: $"{TwimlAppFriendlyName} {connection.Id[..8]}",
            voiceUrl: voiceUrl,
            voiceMethod: Twilio.Http.HttpMethod.Post,
            statusCallback: statusUrl,
            statusCallbackMethod: Twilio.Http.HttpMethod.Post,
            client: client);

        connection.TwimlAppSid = app.Sid;
        await db.SaveChangesAsync(ct);
        return app.Sid;
    }

    /// <summary>
    /// Rewrites the voice and messaging webhooks of the chosen numbers so inbound
    /// traffic lands here. Numbers the user did not choose are left untouched.
    /// </summary>
    public async Task<ProvisionResponse> ProvisionNumbersAsync(
        TwilioConnection connection, ProvisionRequest request, CancellationToken ct = default)
    {
        var twimlAppSid = await EnsureTwimlAppAsync(connection, ct);
        var client = clientFactory.Create(connection);

        var updated = new List<string>();
        var failures = new List<string>();

        foreach (var sid in request.PhoneNumberSids.Distinct())
        {
            try
            {
                await IncomingPhoneNumberResource.UpdateAsync(
                    pathSid: sid,
                    voiceUrl: urls.Voice(connection),
                    voiceMethod: Twilio.Http.HttpMethod.Post,
                    statusCallback: urls.VoiceStatus(connection),
                    statusCallbackMethod: Twilio.Http.HttpMethod.Post,
                    smsUrl: urls.Sms(connection),
                    smsMethod: Twilio.Http.HttpMethod.Post,
                    client: client);
                updated.Add(sid);
            }
            catch (ApiException ex)
            {
                logger.LogWarning(ex, "Could not provision number {Sid}", sid);
                failures.Add($"{sid}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.FallbackForwardNumber))
        {
            connection.FallbackForwardNumber = request.FallbackForwardNumber;
            await db.SaveChangesAsync(ct);
        }

        return new ProvisionResponse(twimlAppSid, updated, failures);
    }
}

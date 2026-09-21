using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Thin translation layer over the Twilio REST API. Everything the app needs about
/// numbers, messages and calls goes through here so that Twilio's SDK types never
/// leak into the HTTP contract.
/// </summary>
public class TwilioApiService(
    TwilioClientFactory clientFactory,
    WebhookUrlBuilder urls,
    ILogger<TwilioApiService> logger)
{
    public async Task<IReadOnlyList<PhoneNumberDto>> ListNumbersAsync(
        TwilioConnection connection, CancellationToken ct = default)
    {
        var client = clientFactory.Create(connection);
        var expectedVoice = urls.Voice(connection).ToString();
        var expectedSms = urls.Sms(connection).ToString();

        var numbers = await IncomingPhoneNumberResource.ReadAsync(client: client);

        return numbers.Select(n => new PhoneNumberDto(
                Sid: n.Sid,
                PhoneNumber: n.PhoneNumber?.ToString() ?? string.Empty,
                FriendlyName: n.FriendlyName ?? string.Empty,
                SmsEnabled: n.Capabilities?.Sms ?? false,
                VoiceEnabled: n.Capabilities?.Voice ?? false,
                MmsEnabled: n.Capabilities?.Mms ?? false,
                VoiceUrl: n.VoiceUrl?.ToString(),
                SmsUrl: n.SmsUrl?.ToString(),
                WiredToThisBackend:
                    string.Equals(n.VoiceUrl?.ToString(), expectedVoice, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(n.SmsUrl?.ToString(), expectedSms, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.PhoneNumber, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<MessageDto> SendMessageAsync(
        TwilioConnection connection, SendMessageRequest request, CancellationToken ct = default)
    {
        var client = clientFactory.Create(connection);

        var message = await MessageResource.CreateAsync(
            to: new PhoneNumber(request.To),
            from: new PhoneNumber(request.From),
            body: request.Body,
            statusCallback: urls.SmsStatus(connection),
            client: client);

        return ToDto(message, Array.Empty<string>());
    }

    /// <summary>
    /// Fetches the message history for one of the account's numbers. Twilio indexes
    /// by To and From separately, so an inbox needs both halves fetched and merged.
    /// </summary>
    public async Task<IReadOnlyList<MessageDto>> ListMessagesAsync(
        TwilioConnection connection,
        string ownedNumber,
        string? peerNumber,
        int limit,
        CancellationToken ct = default)
    {
        var client = clientFactory.Create(connection);

        var inboundTask = MessageResource.ReadAsync(
            to: new PhoneNumber(ownedNumber),
            from: peerNumber is null ? null : new PhoneNumber(peerNumber),
            limit: limit,
            client: client);

        var outboundTask = MessageResource.ReadAsync(
            from: new PhoneNumber(ownedNumber),
            to: peerNumber is null ? null : new PhoneNumber(peerNumber),
            limit: limit,
            client: client);

        await Task.WhenAll(inboundTask, outboundTask);

        return inboundTask.Result.Concat(outboundTask.Result)
            .DistinctBy(m => m.Sid)
            .Select(m => ToDto(m, Array.Empty<string>()))
            .OrderByDescending(m => m.SentAt ?? DateTimeOffset.MinValue)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Groups the recent message history of one owned number into per-peer threads,
    /// which is what the app's inbox list renders.
    /// </summary>
    public async Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(
        TwilioConnection connection, string ownedNumber, int limit, CancellationToken ct = default)
    {
        var messages = await ListMessagesAsync(connection, ownedNumber, null, limit, ct);

        return messages
            .GroupBy(m => string.Equals(m.From, ownedNumber, StringComparison.OrdinalIgnoreCase)
                ? m.To
                : m.From)
            .Select(g =>
            {
                var latest = g.OrderByDescending(m => m.SentAt ?? DateTimeOffset.MinValue).First();
                return new ConversationDto(
                    OwnedNumber: ownedNumber,
                    PeerNumber: g.Key,
                    LastBody: latest.Body,
                    LastDirection: latest.Direction,
                    LastAt: latest.SentAt,
                    MessageCount: g.Count());
            })
            .OrderByDescending(c => c.LastAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public async Task<IReadOnlyList<CallDto>> ListCallsAsync(
        TwilioConnection connection, string? ownedNumber, int limit, CancellationToken ct = default)
    {
        var client = clientFactory.Create(connection);

        if (ownedNumber is null)
        {
            var all = await CallResource.ReadAsync(limit: limit, client: client);
            return all.Select(ToDto).ToList();
        }

        var toTask = CallResource.ReadAsync(to: ownedNumber, limit: limit, client: client);
        var fromTask = CallResource.ReadAsync(from: ownedNumber, limit: limit, client: client);
        await Task.WhenAll(toTask, fromTask);

        return toTask.Result.Concat(fromTask.Result)
            .DistinctBy(c => c.Sid)
            .Select(ToDto)
            .OrderByDescending(c => c.StartedAt ?? DateTimeOffset.MinValue)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Places a call without the Voice SDK: Twilio rings <paramref name="request"/>.BridgeTo
    /// (the user's real handset) and, once answered, dials the destination. This is the
    /// fallback path for platforms where the Voice SDK has no implementation.
    /// </summary>
    public async Task<CallDto> DialOutAsync(
        TwilioConnection connection, DialOutRequest request, CancellationToken ct = default)
    {
        var client = clientFactory.Create(connection);

        var twiml = new Twilio.TwiML.VoiceResponse()
            .Say($"Connecting you now.")
            .Append(new Twilio.TwiML.Voice.Dial(callerId: request.From)
                .Number(new PhoneNumber(request.To)));

        var call = await CallResource.CreateAsync(
            to: new PhoneNumber(request.BridgeTo),
            from: new PhoneNumber(request.From),
            twiml: new Twilio.Types.Twiml(twiml.ToString()),
            client: client);

        return ToDto(call);
    }

    /// <summary>
    /// Verifies a credential set by making the cheapest authenticated call Twilio offers,
    /// and returns the account's friendly name for display.
    /// </summary>
    public async Task<string> ValidateCredentialsAsync(
        string accountSid, string apiKeySid, string apiKeySecret, CancellationToken ct = default)
    {
        var client = clientFactory.Create(accountSid, apiKeySid, apiKeySecret);
        try
        {
            var account = await AccountResource.FetchAsync(pathSid: accountSid, client: client);
            return account.FriendlyName ?? accountSid;
        }
        catch (ApiException ex)
        {
            logger.LogWarning(ex, "Twilio rejected credentials for account {AccountSid}", accountSid);
            throw new TwilioCredentialException(ex.Message);
        }
    }

    private static MessageDto ToDto(MessageResource m, IReadOnlyList<string> mediaUrls) => new(
        Sid: m.Sid,
        From: m.From?.ToString() ?? string.Empty,
        To: m.To ?? string.Empty,
        Body: m.Body,
        Direction: m.Direction?.ToString() ?? "unknown",
        Status: m.Status?.ToString() ?? "unknown",
        NumMedia: int.TryParse(m.NumMedia, out var n) ? n : 0,
        MediaUrls: mediaUrls,
        SentAt: m.DateSent ?? m.DateCreated,
        ErrorMessage: m.ErrorMessage);

    private static CallDto ToDto(CallResource c) => new(
        Sid: c.Sid,
        From: c.From ?? string.Empty,
        To: c.To ?? string.Empty,
        Direction: c.Direction ?? "unknown",
        Status: c.Status?.ToString() ?? "unknown",
        DurationSeconds: int.TryParse(c.Duration, out var d) ? d : null,
        StartedAt: c.StartTime ?? c.DateCreated);
}

public class TwilioCredentialException(string message) : Exception(message);

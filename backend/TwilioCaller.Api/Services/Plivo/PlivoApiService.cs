using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services.Plivo;

/// <summary>
/// The Plivo counterpart of <see cref="TwilioApiService"/>: numbers, messages and
/// calls in the app's own DTOs, with phone numbers normalised to E.164 on the way out.
/// </summary>
public class PlivoApiService(
    PlivoClient plivo,
    AppDbContext db,
    WebhookUrlBuilder urls,
    ILogger<PlivoApiService> logger) : ITelephonyApi
{
    /// <summary>
    /// Verifies an Auth ID / Auth Token pair by fetching the account they belong to,
    /// and returns the account's name for display.
    /// </summary>
    public async Task<string> ValidateCredentialsAsync(
        string authId, string authToken, CancellationToken ct = default)
    {
        try
        {
            var account = await plivo.SendAsync(
                authId, authToken, HttpMethod.Get, string.Empty, null, null, ct);
            var name = account.Str("name");
            return string.IsNullOrWhiteSpace(name) ? authId : name;
        }
        catch (PlivoApiException ex)
        {
            logger.LogWarning(ex, "Plivo rejected credentials for account {AuthId}", authId);
            throw new PlivoCredentialException(ex.Message);
        }
    }

    public async Task<IReadOnlyList<PhoneNumberDto>> ListNumbersAsync(
        TwilioConnection connection, CancellationToken ct = default)
    {
        // A Plivo number has no webhook URLs of its own: it points at an Application,
        // and the Application holds them. "Wired" means "attached to our application".
        var ourApp = string.IsNullOrEmpty(connection.TwimlAppSid)
            ? null
            : $"/Application/{connection.TwimlAppSid}/";

        var numbers = await ReadAllAsync(connection, "Number/", [], 400, ct);

        return numbers.Select(n =>
            {
                var digits = n.Str("number") ?? string.Empty;
                var application = n.Str("application");
                var wired = ourApp is not null && application is not null &&
                            application.EndsWith(ourApp, StringComparison.Ordinal);

                return new PhoneNumberDto(
                    Sid: digits,
                    PhoneNumber: PlivoNumbers.FromPlivo(digits),
                    FriendlyName: n.Str("alias") ?? string.Empty,
                    SmsEnabled: n.Flag("sms_enabled"),
                    VoiceEnabled: n.Flag("voice_enabled"),
                    MmsEnabled: n.Flag("mms_enabled"),
                    VoiceUrl: wired ? urls.Voice(connection).ToString() : null,
                    SmsUrl: wired ? urls.Sms(connection).ToString() : null,
                    WiredToThisBackend: wired);
            })
            .OrderBy(n => n.PhoneNumber, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<MessageDto> SendMessageAsync(
        TwilioConnection connection, SendMessageRequest request, CancellationToken ct = default)
    {
        var response = await plivo.PostAsync(connection, "Message/", new
        {
            src = PlivoNumbers.ToPlivo(request.From),
            dst = PlivoNumbers.ToPlivo(request.To),
            text = request.Body,
            url = urls.SmsStatus(connection).ToString(),
            method = "POST",
        }, ct);

        // Plivo never hands the text back, so the copy kept here is the history.
        var stored = new StoredMessage
        {
            ConnectionId = connection.Id,
            Sid = response.Str("message_uuid") ?? Guid.NewGuid().ToString(),
            FromNumber = request.From,
            ToNumber = request.To,
            Body = request.Body,
            Direction = "outbound",
            Status = "queued",
        };
        db.StoredMessages.Add(stored);
        await db.SaveChangesAsync(ct);

        return ToDto(stored);
    }

    /// <summary>
    /// Served from this backend's own log rather than Plivo, whose message API has
    /// neither the text nor a by-number filter. History therefore starts when the
    /// account was connected here.
    /// </summary>
    public async Task<IReadOnlyList<MessageDto>> ListMessagesAsync(
        TwilioConnection connection,
        string ownedNumber,
        string? peerNumber,
        int limit,
        CancellationToken ct = default)
    {
        var query = db.StoredMessages.Where(m => m.ConnectionId == connection.Id);

        query = peerNumber is null
            ? query.Where(m => m.FromNumber == ownedNumber || m.ToNumber == ownedNumber)
            : query.Where(m =>
                (m.FromNumber == ownedNumber && m.ToNumber == peerNumber) ||
                (m.FromNumber == peerNumber && m.ToNumber == ownedNumber));

        var messages = await query
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);

        return messages.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(
        TwilioConnection connection, string ownedNumber, int limit, CancellationToken ct = default) =>
        Conversations.Group(
            await ListMessagesAsync(connection, ownedNumber, null, limit, ct), ownedNumber);

    public async Task<IReadOnlyList<CallDto>> ListCallsAsync(
        TwilioConnection connection, string? ownedNumber, int limit, CancellationToken ct = default)
    {
        if (ownedNumber is null)
        {
            return (await ReadAllAsync(connection, "Call/", [], limit, ct)).Select(ToDto).ToList();
        }

        var digits = PlivoNumbers.ToPlivo(ownedNumber);
        var toTask = ReadAllAsync(connection, "Call/", [new("to_number", digits)], limit, ct);
        var fromTask = ReadAllAsync(connection, "Call/", [new("from_number", digits)], limit, ct);
        await Task.WhenAll(toTask, fromTask);

        return toTask.Result.Concat(fromTask.Result)
            .Select(ToDto)
            .DistinctBy(c => c.Sid)
            .OrderByDescending(c => c.StartedAt ?? DateTimeOffset.MinValue)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Places a call without the Browser SDK: Plivo rings the user's own handset and,
    /// once it answers, fetches the bridge URL, which dials the destination.
    /// </summary>
    public async Task<CallDto> DialOutAsync(
        TwilioConnection connection, DialOutRequest request, CancellationToken ct = default)
    {
        var from = PlivoNumbers.ToPlivo(request.From);
        var to = PlivoNumbers.ToPlivo(request.To);

        var response = await plivo.PostAsync(connection, "Call/", new
        {
            from,
            to = PlivoNumbers.ToPlivo(request.BridgeTo),
            answer_url = urls.PlivoBridge(connection, to, from).ToString(),
            answer_method = "POST",
            hangup_url = urls.VoiceStatus(connection).ToString(),
            hangup_method = "POST",
        }, ct);

        return new CallDto(
            Sid: response.Str("request_uuid") ?? string.Empty,
            From: request.From,
            To: request.BridgeTo,
            Direction: "outbound",
            Status: "queued",
            DurationSeconds: null,
            StartedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>Walks Plivo's offset pagination until <paramref name="limit"/> objects are in hand.</summary>
    private async Task<List<JsonElement>> ReadAllAsync(
        TwilioConnection connection,
        string path,
        KeyValuePair<string, string?>[] filters,
        int limit,
        CancellationToken ct)
    {
        var results = new List<JsonElement>();

        while (results.Count < limit)
        {
            var pageSize = Math.Min(PlivoClient.PageSize, limit - results.Count);
            var query = filters.Concat(
            [
                new KeyValuePair<string, string?>("limit", pageSize.ToString()),
                new KeyValuePair<string, string?>("offset", results.Count.ToString()),
            ]);

            var page = (await plivo.GetAsync(connection, path, query, ct)).Objects().ToList();
            results.AddRange(page);

            if (page.Count < pageSize)
            {
                break;
            }
        }

        return results;
    }

    private static MessageDto ToDto(StoredMessage m) => new(
        Sid: m.Sid,
        From: m.FromNumber,
        To: m.ToNumber,
        Body: m.Body,
        Direction: m.Direction,
        Status: m.Status,
        NumMedia: m.NumMedia,
        MediaUrls: Array.Empty<string>(),
        SentAt: m.SentAt,
        ErrorMessage: null);

    private static CallDto ToDto(JsonElement c)
    {
        var duration = c.Int("call_duration") ?? c.Int("bill_duration");

        return new CallDto(
            Sid: c.Str("call_uuid") ?? string.Empty,
            From: PlivoNumbers.FromPlivo(c.Str("from_number")),
            To: PlivoNumbers.FromPlivo(c.Str("to_number")),
            Direction: c.Str("call_direction") ?? "unknown",
            Status: CallStatus(c.Str("call_state"), c.Str("hangup_cause_name"), duration),
            DurationSeconds: duration,
            StartedAt: c.Time("initiation_time") ?? c.Time("answer_time") ?? c.Time("end_time"));
    }

    /// <summary>
    /// Folds Plivo's call vocabulary onto the statuses the app already understands
    /// from Twilio (completed, busy, no-answer, failed, canceled).
    /// </summary>
    public static string CallStatus(string? state, string? hangupCause, int? duration)
    {
        var raw = (state ?? hangupCause ?? string.Empty).ToLowerInvariant().Replace('_', ' ');

        if (raw.Contains("busy")) return "busy";
        if (raw.Contains("no answer") || raw.Contains("timeout")) return "no-answer";
        if (raw.Contains("cancel")) return "canceled";
        if (raw.Contains("fail") || raw.Contains("reject") || raw.Contains("error")) return "failed";
        if (raw.Contains("answer") || raw.Contains("normal") || duration > 0) return "completed";

        return raw.Length == 0 ? "unknown" : raw.Replace(' ', '-');
    }
}

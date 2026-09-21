namespace TwilioCaller.Api.Contracts;

// ---------- auth ----------

public record ConnectRequest(
    string AccountSid,
    string ApiKeySid,
    string ApiKeySecret,
    string? AuthToken,
    string? Platform,
    bool SupportsVoip);

public record ConnectResponse(
    string SessionToken,
    DateTimeOffset ExpiresAt,
    string ConnectionId,
    string AccountSid,
    string FriendlyName,
    string Identity,
    bool VoiceReady);

public record SessionResponse(
    string ConnectionId,
    string AccountSid,
    string FriendlyName,
    string Identity,
    bool VoiceReady,
    string? TwimlAppSid,
    string? FallbackForwardNumber);

// ---------- numbers ----------

public record PhoneNumberDto(
    string Sid,
    string PhoneNumber,
    string FriendlyName,
    bool SmsEnabled,
    bool VoiceEnabled,
    bool MmsEnabled,
    string? VoiceUrl,
    string? SmsUrl,
    bool WiredToThisBackend);

public record ProvisionRequest(IReadOnlyList<string> PhoneNumberSids, string? FallbackForwardNumber);

public record ProvisionResponse(
    string TwimlAppSid,
    IReadOnlyList<string> UpdatedNumberSids,
    IReadOnlyList<string> Failures);

// ---------- messaging ----------

public record SendMessageRequest(string From, string To, string Body);

public record MessageDto(
    string Sid,
    string From,
    string To,
    string? Body,
    string Direction,
    string Status,
    int NumMedia,
    IReadOnlyList<string> MediaUrls,
    DateTimeOffset? SentAt,
    string? ErrorMessage);

public record ConversationDto(
    string OwnedNumber,
    string PeerNumber,
    string? LastBody,
    string LastDirection,
    DateTimeOffset? LastAt,
    int MessageCount);

// ---------- voice ----------

public record VoiceTokenResponse(string Token, string Identity, DateTimeOffset ExpiresAt);

public record DialOutRequest(string From, string To, string BridgeTo);

public record CallDto(
    string Sid,
    string From,
    string To,
    string Direction,
    string Status,
    int? DurationSeconds,
    DateTimeOffset? StartedAt);

// ---------- realtime replay ----------

public record InboundEventDto(
    long Id,
    string Kind,
    string Sid,
    string From,
    string To,
    string? Body,
    string? Status,
    int NumMedia,
    DateTimeOffset ReceivedAt);

public record ApiError(string Error, string? Detail = null);

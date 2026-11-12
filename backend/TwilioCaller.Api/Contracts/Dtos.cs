namespace TwilioCaller.Api.Contracts;

// ---------- auth ----------

/// <summary>
/// Twilio sends accountSid + apiKeySid + apiKeySecret (authToken optional).
/// Plivo sends provider "plivo" with its Auth ID as accountSid and its Auth Token
/// as authToken; it has no API keys. An absent provider means Twilio.
/// </summary>
public record ConnectRequest(
    string AccountSid,
    string? ApiKeySid,
    string? ApiKeySecret,
    string? AuthToken,
    string? Platform,
    bool SupportsVoip,
    string? Provider = null);

public record ConnectResponse(
    string SessionToken,
    DateTimeOffset ExpiresAt,
    string ConnectionId,
    string AccountSid,
    string FriendlyName,
    string Identity,
    bool VoiceReady,
    string Provider = "twilio");

public record SessionResponse(
    string ConnectionId,
    string AccountSid,
    string FriendlyName,
    string Identity,
    bool VoiceReady,
    string? TwimlAppSid,
    string? FallbackForwardNumber,
    string Provider = "twilio");

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

/// <summary>
/// What the app needs to bring its voice SDK up. Twilio: a signed access token.
/// Plivo: the SIP endpoint this device signs in to the Browser SDK as; its password
/// is generated per device and is useless for anything but that endpoint.
/// </summary>
public record VoiceTokenResponse(
    string Token,
    string Identity,
    DateTimeOffset ExpiresAt,
    string Provider = "twilio",
    string? Username = null,
    string? Password = null);

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

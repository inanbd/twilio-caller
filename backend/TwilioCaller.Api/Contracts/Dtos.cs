namespace TwilioCaller.Api.Contracts;

// ---------- auth ----------

public record RegisterRequest(
    string Email,
    string Password,
    string? DisplayName,
    string? Platform,
    bool SupportsVoip);

public record LoginRequest(
    string Email,
    string Password,
    string? Platform,
    bool SupportsVoip);

public record AuthResponse(
    string SessionToken,
    DateTimeOffset ExpiresAt,
    string UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string Identity);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record SessionResponse(
    string UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string Identity,
    TwilioConnectionDto? Connection);

// ---------- twilio connection ----------

public record TwilioConnectRequest(
    string AccountSid,
    string ApiKeySid,
    string ApiKeySecret,
    string? AuthToken);

public record TwilioConnectionDto(
    string ConnectionId,
    string AccountSid,
    string FriendlyName,
    bool VoiceReady,
    string? TwimlAppSid,
    string? FallbackForwardNumber,
    DateTimeOffset CreatedAt);

// ---------- admin ----------

public record AdminUserSummary(
    string Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Disabled,
    DateTimeOffset CreatedAt,
    string? AccountSid,
    string? TwilioFriendlyName,
    int DeviceCount,
    DateTimeOffset? LastSeenAt);

public record AdminDeviceDto(
    string Id,
    string Identity,
    string Platform,
    bool SupportsVoip,
    DateTimeOffset LastSeenAt);

public record AdminUserDetail(
    string Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Disabled,
    DateTimeOffset CreatedAt,
    TwilioConnectionDto? Connection,
    IReadOnlyList<AdminDeviceDto> Devices);

public record AdminCreateUserRequest(
    string Email,
    string Password,
    string? DisplayName,
    bool IsAdministrator);

public record AdminSetRoleRequest(bool IsAdministrator);

public record AdminSetDisabledRequest(bool Disabled);

public record AdminResetPasswordRequest(string NewPassword);

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

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace TwilioCaller.Api.Data;

/// <summary>
/// An account someone registers, from the app or the portal. Identity owns the
/// password hash, lockout and security stamp; everything the account manages — its
/// Twilio connection and its devices — hangs off this row.
/// </summary>
public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One connected Twilio account, owned by exactly one user. The API key secret is
/// never stored in the clear: <see cref="ApiKeySecretCipher"/> holds an AES-GCM
/// envelope produced by <see cref="Services.CredentialProtector"/>.
/// </summary>
public class TwilioConnection
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string UserId { get; set; } = string.Empty;
    public AppUser? User { get; set; }

    public string AccountSid { get; set; } = string.Empty;
    public string ApiKeySid { get; set; } = string.Empty;
    public string ApiKeySecretCipher { get; set; } = string.Empty;

    /// <summary>Optional. Only needed to verify X-Twilio-Signature on webhooks.</summary>
    public string? AuthTokenCipher { get; set; }

    /// <summary>
    /// High-entropy path segment embedded in every webhook URL. Twilio is the only
    /// party that ever learns it, so it acts as a bearer secret for webhook callers
    /// when no auth token is available for signature validation.
    /// </summary>
    public string WebhookKey { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>TwiML Application SID used as the Voice SDK's outgoing application.</summary>
    public string? TwimlAppSid { get; set; }

    /// <summary>E.164 number to bridge calls to when the caller has no VoIP client.</summary>
    public string? FallbackForwardNumber { get; set; }

    /// <summary>
    /// Twilio Push Credential SIDs (CR...). Without one the Voice SDK can only ring
    /// while the app is in the foreground, because Twilio has no way to wake it.
    /// Created in the Twilio console from an FCM server key or an APNs VoIP cert.
    /// </summary>
    public string? AndroidPushCredentialSid { get; set; }

    public string? ApplePushCredentialSid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A logged-in app or portal instance, identified to Twilio Voice by
/// <see cref="Identity"/>. Devices belong to the user, not the Twilio connection,
/// because a user logs in before they have connected a Twilio account.
/// </summary>
public class DeviceRegistration
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string UserId { get; set; } = string.Empty;
    public AppUser? User { get; set; }

    /// <summary>Twilio Voice client identity, e.g. "user_ab12cd". Must match ^[A-Za-z0-9_-]+$.</summary>
    public string Identity { get; set; } = string.Empty;

    public string Platform { get; set; } = "unknown";
    public bool SupportsVoip { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Inbound SMS and call events captured from webhooks. Twilio remains the source of
/// truth for history; this table exists so the app can replay anything it missed
/// while offline, and so the realtime feed has a durable backing store.
/// </summary>
public class InboundEvent
{
    [Key]
    public long Id { get; set; }

    public string ConnectionId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // message | call
    public string TwilioSid { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    public string ToNumber { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? Status { get; set; }
    public int NumMedia { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

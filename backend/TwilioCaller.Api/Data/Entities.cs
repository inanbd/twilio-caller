using System.ComponentModel.DataAnnotations;

namespace TwilioCaller.Api.Data;

/// <summary>Names of the telephony providers a connection can belong to.</summary>
public static class Providers
{
    public const string Twilio = "twilio";
    public const string Plivo = "plivo";

    /// <summary>Maps a client-supplied value onto a known provider; absent means Twilio.</summary>
    public static string? Normalise(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or Twilio => Twilio,
        Plivo => Plivo,
        _ => null,
    };
}

/// <summary>
/// One connected provider account, Twilio or Plivo (the class keeps its original
/// name; <see cref="Provider"/> says which). Secrets are never stored in the clear:
/// the cipher columns hold AES-GCM envelopes produced by
/// <see cref="Services.CredentialProtector"/>.
/// </summary>
public class TwilioConnection
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary><see cref="Providers.Twilio"/> or <see cref="Providers.Plivo"/>.</summary>
    public string Provider { get; set; } = Providers.Twilio;

    /// <summary>Twilio Account SID (AC...), or the Plivo Auth ID (MA.../SA...).</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>Twilio only. Plivo has no API keys and leaves these empty.</summary>
    public string ApiKeySid { get; set; } = string.Empty;
    public string ApiKeySecretCipher { get; set; } = string.Empty;

    /// <summary>
    /// Twilio: optional, only needed to verify X-Twilio-Signature on webhooks.
    /// Plivo: required — it is the account's REST credential as well as the key
    /// its webhook signatures are made with.
    /// </summary>
    public string? AuthTokenCipher { get; set; }

    /// <summary>
    /// High-entropy path segment embedded in every webhook URL. Twilio is the only
    /// party that ever learns it, so it acts as a bearer secret for webhook callers
    /// when no auth token is available for signature validation.
    /// </summary>
    public string WebhookKey { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// Twilio: TwiML Application SID used as the Voice SDK's outgoing application.
    /// Plivo: the id of the Application our numbers and endpoints are attached to.
    /// </summary>
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

    public List<DeviceRegistration> Devices { get; set; } = new();
}

/// <summary>A logged-in app instance, identified to Twilio Voice by <see cref="Identity"/>.</summary>
public class DeviceRegistration
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string ConnectionId { get; set; } = string.Empty;
    public TwilioConnection? Connection { get; set; }

    /// <summary>Twilio Voice client identity, e.g. "user_ab12cd". Must match ^[A-Za-z0-9_-]+$.</summary>
    public string Identity { get; set; } = string.Empty;

    public string Platform { get; set; } = "unknown";
    public bool SupportsVoip { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Plivo only. The Browser SDK signs in as a SIP endpoint, so each device that
    /// can take in-app audio gets one of its own. The username is what inbound
    /// calls are dialled to; the password is generated here and stored encrypted.
    /// </summary>
    public string? PlivoEndpointId { get; set; }
    public string? PlivoEndpointUsername { get; set; }
    public string? PlivoEndpointPasswordCipher { get; set; }
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

/// <summary>
/// Message history for Plivo connections. Plivo's API returns neither the text of a
/// message nor a way to list messages by number, so unlike Twilio it cannot serve as
/// the system of record: what the app shows is what passed through this backend.
/// </summary>
public class StoredMessage
{
    [Key]
    public long Id { get; set; }

    public string ConnectionId { get; set; } = string.Empty;
    public string Sid { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    public string ToNumber { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string Direction { get; set; } = "outbound"; // inbound | outbound
    public string Status { get; set; } = "queued";
    public int NumMedia { get; set; }
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

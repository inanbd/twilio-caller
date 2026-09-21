using Twilio.Jwt.AccessToken;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Mints the short-lived Twilio Access Tokens the Voice SDK needs. This is the single
/// reason a backend exists at all: signing requires the API key secret, which must
/// never ship inside the mobile binary.
/// </summary>
public class VoiceTokenService(CredentialProtector protector, IConfiguration config)
{
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(
        double.TryParse(config["Voice:TokenMinutes"], out var m) ? m : 60);

    public (string Token, DateTimeOffset ExpiresAt) Issue(TwilioConnection connection, string identity)
    {
        if (string.IsNullOrEmpty(connection.TwimlAppSid))
        {
            throw new InvalidOperationException(
                "This connection has no TwiML application yet. Provision numbers first.");
        }

        var grant = new VoiceGrant
        {
            OutgoingApplicationSid = connection.TwimlAppSid,
            IncomingAllow = true,
        };

        var expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
        var token = new Token(
            accountSid: connection.AccountSid,
            signingKeySid: connection.ApiKeySid,
            secret: protector.Unprotect(connection.ApiKeySecretCipher),
            identity: identity,
            expiration: expiresAt.UtcDateTime,
            grants: new HashSet<IGrant> { grant });

        return (token.ToJwt(), expiresAt);
    }
}

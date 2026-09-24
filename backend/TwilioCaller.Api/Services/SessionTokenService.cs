using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Issues the bearer tokens the Flutter app uses to talk to this API. These are
/// ours, not Twilio's: they carry only a connection id and a device identity, so a
/// leaked app token grants access to this backend, never to the raw Twilio secret.
/// </summary>
public class SessionTokenService
{
    public const string UserIdClaim = "uid";
    public const string IdentityClaim = "ident";

    /// <summary>
    /// Identity's security stamp at issue time. It rotates on password change, reset
    /// and role change, so comparing it per-request revokes stale sessions.
    /// </summary>
    public const string StampClaim = "stamp";

    private readonly SymmetricSecurityKey _key;
    private readonly TimeSpan _lifetime;

    public string Issuer { get; }
    public string Audience { get; }

    public SessionTokenService(IConfiguration config)
    {
        var secret = config["Security:SessionSigningKey"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            throw new InvalidOperationException(
                "Security:SessionSigningKey must be set to at least 32 characters. " +
                "Generate one with: openssl rand -base64 48");
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        Issuer = config["Security:Issuer"] ?? "twilio-caller";
        Audience = config["Security:Audience"] ?? "twilio-caller-app";
        _lifetime = TimeSpan.FromDays(
            double.TryParse(config["Security:SessionDays"], out var d) ? d : 30);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
    };

    public (string Token, DateTimeOffset ExpiresAt) Issue(
        string userId, string identity, IEnumerable<string>? roles = null,
        string? securityStamp = null)
    {
        var expires = DateTimeOffset.UtcNow.Add(_lifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(UserIdClaim, userId),
            new(IdentityClaim, identity),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("n")),
        };

        if (!string.IsNullOrEmpty(securityStamp))
        {
            claims.Add(new Claim(StampClaim, securityStamp));
        }

        // ClaimTypes.Role round-trips through the JWT handler's default claim map, so
        // ClaimsPrincipal.IsInRole and RequireRole both see it after validation.
        foreach (var role in roles ?? Array.Empty<string>())
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        };

        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(handler.CreateToken(descriptor)), expires);
    }

    /// <summary>
    /// Builds a Voice SDK client identity. Twilio restricts identities to
    /// [A-Za-z0-9_-], and it appears in TwiML, so we generate rather than accept one.
    /// </summary>
    public static string NewDeviceIdentity(string platform)
    {
        var safePlatform = new string(platform
            .Where(char.IsLetterOrDigit)
            .Take(12)
            .ToArray())
            .ToLowerInvariant();

        if (safePlatform.Length == 0)
        {
            safePlatform = "device";
        }

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        return $"{safePlatform}_{suffix}";
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using TwilioCaller.Api.Services;
using Xunit;

namespace TwilioCaller.Tests;

public class CredentialProtectorTests
{
    private static CredentialProtector Build() => new(Config(
        ("Security:MasterKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));

    internal static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void RoundTrips_A_Secret()
    {
        var protector = Build();
        const string secret = "an-api-key-secret-value";

        var cipher = protector.Protect(secret);

        Assert.NotEqual(secret, cipher);
        Assert.Equal(secret, protector.Unprotect(cipher));
    }

    [Fact]
    public void Produces_A_Different_Ciphertext_Each_Time()
    {
        var protector = Build();

        // A fresh nonce per call means identical secrets must not collide, otherwise
        // the database would leak which accounts share a credential.
        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void Rejects_A_Tampered_Ciphertext()
    {
        var protector = Build();
        var cipher = Convert.FromBase64String(protector.Protect("secret"));
        cipher[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(Convert.ToBase64String(cipher)));
    }

    [Fact]
    public void Will_Not_Start_Without_A_Master_Key()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new CredentialProtector(Config(("Security:MasterKey", ""))));

        Assert.Contains("MasterKey", ex.Message);
    }

    [Fact]
    public void Rejects_A_Master_Key_Of_The_Wrong_Length()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new CredentialProtector(
            Config(("Security:MasterKey", Convert.ToBase64String(new byte[16])))));

        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void Unprotecting_Null_Yields_Null()
    {
        Assert.Null(Build().UnprotectOrNull(null));
    }
}

public class SessionTokenServiceTests
{
    private static SessionTokenService Build() => new(CredentialProtectorTests.Config(
        ("Security:SessionSigningKey", "a-signing-key-that-is-definitely-long-enough-32"),
        ("Security:Issuer", "twilio-caller"),
        ("Security:Audience", "twilio-caller-app")));

    [Fact]
    public void Issued_Token_Validates_And_Carries_Its_Claims()
    {
        var service = Build();
        var (token, expiresAt) = service.Issue(
            "user-1", "android_abc123", new[] { "Administrator" }, "stamp-1");

        var principal = new JwtSecurityTokenHandler()
            .ValidateToken(token, service.ValidationParameters, out _);

        Assert.Equal("user-1",
            principal.FindFirst(SessionTokenService.UserIdClaim)?.Value);
        Assert.Equal("android_abc123",
            principal.FindFirst(SessionTokenService.IdentityClaim)?.Value);
        Assert.Equal("stamp-1",
            principal.FindFirst(SessionTokenService.StampClaim)?.Value);
        Assert.True(principal.IsInRole("Administrator"));
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void A_Token_Without_A_Role_Grants_None()
    {
        var service = Build();
        var (token, _) = service.Issue("user-1", "android_abc123");

        var principal = new JwtSecurityTokenHandler()
            .ValidateToken(token, service.ValidationParameters, out _);

        Assert.False(principal.IsInRole("Administrator"));
    }

    [Fact]
    public void A_Token_From_Another_Key_Is_Rejected()
    {
        var (token, _) = Build().Issue("user-1", "android_abc123");

        var other = new SessionTokenService(CredentialProtectorTests.Config(
            ("Security:SessionSigningKey", "a-completely-different-key-also-long-enough!")));

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler()
            .ValidateToken(token, other.ValidationParameters, out _));
    }

    [Fact]
    public void Refuses_A_Weak_Signing_Key()
    {
        Assert.Throws<InvalidOperationException>(() => new SessionTokenService(
            CredentialProtectorTests.Config(("Security:SessionSigningKey", "short"))));
    }

    [Theory]
    [InlineData("android", "android_")]
    [InlineData("iOS", "ios_")]
    [InlineData("web-chrome", "webchrome_")]
    [InlineData("!!!", "device_")]
    public void Device_Identities_Are_Twilio_Safe(string platform, string expectedPrefix)
    {
        var identity = SessionTokenService.NewDeviceIdentity(platform);

        Assert.StartsWith(expectedPrefix, identity);
        // Twilio Voice rejects an identity containing anything outside this set.
        Assert.Matches("^[A-Za-z0-9_-]+$", identity);
    }

    [Fact]
    public void Device_Identities_Are_Unique()
    {
        var identities = Enumerable.Range(0, 200)
            .Select(_ => SessionTokenService.NewDeviceIdentity("android"))
            .ToHashSet();

        Assert.Equal(200, identities.Count);
    }
}

public class WebhookUrlBuilderTests
{
    [Fact]
    public void Builds_Urls_Scoped_To_The_Connection_And_Its_Key()
    {
        var builder = new WebhookUrlBuilder(CredentialProtectorTests.Config(
            ("PublicBaseUrl", "https://example.test/")));

        var connection = new TwilioCaller.Api.Data.TwilioConnection
        {
            Id = "abc",
            WebhookKey = "key123",
        };

        Assert.Equal("https://example.test/webhooks/abc/key123/voice",
            builder.Voice(connection).ToString());
        Assert.Equal("https://example.test/webhooks/abc/key123/sms",
            builder.Sms(connection).ToString());
        Assert.Equal("https://example.test/webhooks/abc/key123/sms/status",
            builder.SmsStatus(connection).ToString());
    }

    [Fact]
    public void Refuses_To_Start_Without_A_Public_Base_Url()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new WebhookUrlBuilder(CredentialProtectorTests.Config(("PublicBaseUrl", ""))));

        Assert.Contains("PublicBaseUrl", ex.Message);
    }

    [Fact]
    public void Refuses_A_Relative_Public_Base_Url()
    {
        Assert.Throws<InvalidOperationException>(
            () => new WebhookUrlBuilder(
                CredentialProtectorTests.Config(("PublicBaseUrl", "/not-absolute"))));
    }
}

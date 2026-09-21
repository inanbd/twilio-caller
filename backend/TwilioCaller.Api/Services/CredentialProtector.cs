using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace TwilioCaller.Api.Services;

/// <summary>
/// AES-GCM envelope encryption for Twilio secrets at rest. The master key comes from
/// configuration (<c>Security:MasterKey</c>, base64 of 32 bytes) so that a stolen
/// database file alone does not yield usable Twilio credentials.
/// </summary>
public class CredentialProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public CredentialProtector(IConfiguration config)
    {
        var configured = config["Security:MasterKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Security:MasterKey is not configured. Generate one with: " +
                "openssl rand -base64 32");
        }

        try
        {
            _key = Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Security:MasterKey must be base64-encoded.");
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Security:MasterKey must decode to 32 bytes, got {_key.Length}.");
        }
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var envelope = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, NonceSize);
        cipher.CopyTo(envelope, NonceSize + TagSize);
        return Convert.ToBase64String(envelope);
    }

    public string Unprotect(string protectedValue)
    {
        var envelope = Convert.FromBase64String(protectedValue);
        if (envelope.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext envelope is truncated.");
        }

        var nonce = envelope.AsSpan(0, NonceSize);
        var tag = envelope.AsSpan(NonceSize, TagSize);
        var cipher = envelope.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    [return: NotNullIfNotNull(nameof(protectedValue))]
    public string? UnprotectOrNull(string? protectedValue) =>
        string.IsNullOrEmpty(protectedValue) ? null : Unprotect(protectedValue);
}

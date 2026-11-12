using System.Security.Cryptography;
using System.Text;

namespace TwilioCaller.Api.Services.Plivo;

/// <summary>
/// Verifies Plivo's webhook signatures, following Plivo's own SDKs step for step.
/// V3 is a base64 HMAC-SHA256, keyed with the auth token, over the callback URL, its
/// parameters and a per-request nonce (in a slightly odd canonical form). V2, which
/// Plivo's messaging callbacks may still use, covers only the URL and the nonce.
/// </summary>
public static class PlivoSignature
{
    public const string SignatureHeader = "X-Plivo-Signature-V3";
    public const string MainAccountSignatureHeader = "X-Plivo-Signature-Ma-V3";
    public const string NonceHeader = "X-Plivo-Signature-V3-Nonce";
    public const string V2SignatureHeader = "X-Plivo-Signature-V2";
    public const string V2NonceHeader = "X-Plivo-Signature-V2-Nonce";

    /// <summary>V2: HMAC-SHA256 over the URL (scheme, host and path only) followed by the nonce.</summary>
    public static string ComputeV2(string url, string nonce, string authToken)
    {
        var uri = new Uri(url);
        var payload = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}{nonce}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    public static bool ValidateV2(string url, string nonce, string authToken, string signature) =>
        !string.IsNullOrEmpty(nonce) &&
        !string.IsNullOrEmpty(signature) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(ComputeV2(url, nonce, authToken)),
            Encoding.UTF8.GetBytes(signature.Trim()));

    public static bool Validate(
        string method,
        string url,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> formParameters,
        string nonce,
        string authToken,
        string signatureHeader)
    {
        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Compute(method, url, formParameters, nonce, authToken));

        // The header may carry several comma-separated signatures; any one matching passes.
        foreach (var candidate in signatureHeader.Split(',', StringSplitOptions.TrimEntries))
        {
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(candidate)))
            {
                return true;
            }
        }

        return false;
    }

    public static string Compute(
        string method,
        string url,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> formParameters,
        string nonce,
        string authToken)
    {
        var payload = $"{BaseString(method, url, formParameters)}.{nonce}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static string BaseString(
        string method,
        string url,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> formParameters)
    {
        var uri = new Uri(url);
        var result = new StringBuilder($"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}");

        var query = ParseQuery(uri.Query);
        var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        var form = isGet
            ? new List<KeyValuePair<string, IReadOnlyList<string>>>()
            : formParameters.ToList();

        // A GET has its parameters in the query string, so there they are signed as
        // "k=v&k=v". A POST signs the query the same way, then its body as "kvkv".
        var queryString = string.Join("&", Sorted(query)
            .SelectMany(p => p.Value.Order(StringComparer.Ordinal).Select(v => $"{p.Key}={v}")));

        if (queryString.Length > 0 || form.Count > 0)
        {
            result.Append('?').Append(queryString);
        }

        if (queryString.Length > 0 && form.Count > 0)
        {
            result.Append('.');
        }

        foreach (var (key, values) in Sorted(form))
        {
            foreach (var value in values.Order(StringComparer.Ordinal))
            {
                result.Append(key).Append(value);
            }
        }

        return result.ToString();
    }

    private static IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> Sorted(
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> parameters) =>
        parameters.OrderBy(p => p.Key, StringComparer.Ordinal);

    private static List<KeyValuePair<string, IReadOnlyList<string>>> ParseQuery(string query) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)
            .Select(p => new KeyValuePair<string, IReadOnlyList<string>>(
                p.Key, p.Value.Select(v => v ?? string.Empty).ToList()))
            .ToList();
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services.Plivo;

/// <summary>A non-2xx answer from Plivo, carrying its own error text.</summary>
public class PlivoApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public class PlivoCredentialException(string message) : Exception(message);

/// <summary>
/// Minimal client for Plivo's REST API. Plivo authenticates every request with HTTP
/// Basic (Auth ID : Auth Token) and takes JSON bodies, so plain HttpClient is all it
/// needs — and, unlike an SDK with static configuration, it stays per-connection.
/// </summary>
public class PlivoClient(
    CredentialProtector protector, IHttpClientFactory httpClientFactory, IConfiguration config)
{
    public const string HttpClientName = "plivo";

    /// <summary>Plivo caps every list endpoint at 20 objects a page.</summary>
    public const int PageSize = 20;

    private readonly string _baseUrl =
        (config["Plivo:ApiBaseUrl"] ?? "https://api.plivo.com").TrimEnd('/');

    public Task<JsonElement> GetAsync(
        TwilioConnection connection, string path,
        IEnumerable<KeyValuePair<string, string?>>? query = null, CancellationToken ct = default) =>
        SendAsync(connection.AccountSid, TokenOf(connection), HttpMethod.Get, path, query, null, ct);

    public Task<JsonElement> PostAsync(
        TwilioConnection connection, string path, object body, CancellationToken ct = default) =>
        SendAsync(connection.AccountSid, TokenOf(connection), HttpMethod.Post, path, null, body, ct);

    public Task<JsonElement> DeleteAsync(
        TwilioConnection connection, string path, CancellationToken ct = default) =>
        SendAsync(connection.AccountSid, TokenOf(connection), HttpMethod.Delete, path, null, null, ct);

    /// <param name="path">Relative to the account, e.g. "Number/" — Plivo wants the trailing slash.</param>
    public async Task<JsonElement> SendAsync(
        string authId,
        string authToken,
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query,
        object? body,
        CancellationToken ct = default)
    {
        var url = new StringBuilder($"{_baseUrl}/v1/Account/{Uri.EscapeDataString(authId)}/{path}");
        var separator = '?';
        foreach (var (key, value) in query ?? [])
        {
            if (value is null)
            {
                continue;
            }

            url.Append(separator).Append(Uri.EscapeDataString(key))
                .Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        using var request = new HttpRequestMessage(method, url.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{authId}:{authToken}")));
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        JsonElement json = default;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                json = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // An HTML error page from an intermediary; the status code still speaks.
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PlivoApiException((int)response.StatusCode, ErrorText(json, response.StatusCode));
        }

        return json;
    }

    private string TokenOf(TwilioConnection connection) =>
        protector.UnprotectOrNull(connection.AuthTokenCipher)
        ?? throw new InvalidOperationException(
            $"Connection {connection.Id} has no Plivo auth token on file.");

    /// <summary>Plivo reports failures as {"error": "..."}, where the value is sometimes an object.</summary>
    private static string ErrorText(JsonElement json, HttpStatusCode status)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "error", "message" })
            {
                if (json.TryGetProperty(name, out var value))
                {
                    return value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : value.GetRawText();
                }
            }
        }

        return status == HttpStatusCode.Unauthorized
            ? "Plivo rejected the Auth ID or Auth Token."
            : $"Plivo answered with HTTP {(int)status}.";
    }
}

/// <summary>Tolerant readers: Plivo is loose about numbers-as-strings and nulls.</summary>
internal static class PlivoJson
{
    public static string? Str(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array when value.GetArrayLength() > 0 => value[0].ToString(),
            _ => null,
        };
    }

    public static bool Flag(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String &&
          bool.TryParse(value.GetString(), out var parsed) && parsed));

    public static int? Int(this JsonElement element, string name) =>
        int.TryParse(element.Str(name), out var parsed) ? parsed : null;

    public static IEnumerable<JsonElement> Objects(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("objects", out var objects) &&
        objects.ValueKind == JsonValueKind.Array
            ? objects.EnumerateArray()
            : [];

    /// <summary>
    /// Plivo writes times as "2024-03-20 23:47:24+05:30", with or without fractional
    /// seconds or an offset. One without an offset is UTC.
    /// </summary>
    public static DateTimeOffset? Time(this JsonElement element, string name)
    {
        var raw = element.Str(name);
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// Plivo writes phone numbers as bare digits ("14155551234") where the rest of this
/// app, like Twilio, uses E.164 ("+14155551234").
/// </summary>
public static class PlivoNumbers
{
    public const string SipDomain = "phone.plivo.com";

    public static string ToPlivo(string number) => number.Trim().TrimStart('+');

    public static string FromPlivo(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        // Short codes and alphanumeric senders are not E.164 and must stay as sent.
        return trimmed.Length >= 8 && trimmed.All(char.IsAsciiDigit) ? $"+{trimmed}" : trimmed;
    }

    public static bool IsSip(string value) =>
        value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase);

    /// <summary>"sip:alice123@phone.plivo.com" → "alice123".</summary>
    public static string SipUser(string value)
    {
        var user = IsSip(value) ? value[4..] : value;
        var at = user.IndexOf('@');
        return at >= 0 ? user[..at] : user;
    }

    public static string SipUri(string username) => $"sip:{username}@{SipDomain}";
}

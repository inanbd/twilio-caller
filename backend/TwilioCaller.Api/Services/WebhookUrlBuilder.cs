using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Produces the absolute webhook URLs handed to Twilio. Twilio dials these from the
/// public internet, so they must be built from the configured public base URL rather
/// than from the incoming request (which may be behind a proxy or a tunnel).
/// </summary>
public class WebhookUrlBuilder
{
    private readonly string _baseUrl;

    public WebhookUrlBuilder(IConfiguration config)
    {
        var configured = config["PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "PublicBaseUrl is not configured. Set it to the https URL Twilio can reach, " +
                "e.g. https://your-app.fly.dev or an ngrok tunnel during development.");
        }

        // On Unix a bare path such as "/webhooks" parses as an absolute file:// URI,
        // so the scheme has to be checked explicitly or a misconfiguration would only
        // surface later as Twilio failing to reach a file:// webhook.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"PublicBaseUrl '{configured}' must be an absolute http or https URL.");
        }

        _baseUrl = parsed.ToString().TrimEnd('/');
    }

    public Uri Voice(TwilioConnection c) => Build(c, "voice");
    public Uri VoiceStatus(TwilioConnection c) => Build(c, "voice/status");
    public Uri Sms(TwilioConnection c) => Build(c, "sms");
    public Uri SmsStatus(TwilioConnection c) => Build(c, "sms/status");

    /// <summary>
    /// The answer URL of a Plivo dial-out. Once the user's handset picks up, Plivo
    /// asks this what to do next; the destination and caller id ride in the path
    /// (as digits) so the URL needs no query string to sign.
    /// </summary>
    public Uri PlivoBridge(TwilioConnection c, string toDigits, string callerIdDigits) =>
        Build(c, $"bridge/{Uri.EscapeDataString(toDigits)}/{Uri.EscapeDataString(callerIdDigits)}");

    // Plivo speaks a different webhook dialect (its own parameters, XML and
    // signature), so its connections get their own set of routes.
    private Uri Build(TwilioConnection c, string leaf) =>
        new(c.Provider == Providers.Plivo
            ? $"{_baseUrl}/webhooks/{c.Id}/{c.WebhookKey}/plivo/{leaf}"
            : $"{_baseUrl}/webhooks/{c.Id}/{c.WebhookKey}/{leaf}");
}

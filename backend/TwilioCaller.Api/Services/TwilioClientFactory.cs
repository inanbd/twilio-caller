using Twilio.Clients;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Builds a per-connection <see cref="ITwilioRestClient"/>. We deliberately avoid
/// Twilio's static <c>TwilioClient.Init</c> because this backend serves more than one
/// Twilio account and static global state would leak credentials across requests.
/// </summary>
public class TwilioClientFactory(CredentialProtector protector, IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "twilio";

    public ITwilioRestClient Create(TwilioConnection connection)
    {
        var secret = protector.Unprotect(connection.ApiKeySecretCipher);
        return Create(connection.AccountSid, connection.ApiKeySid, secret);
    }

    public ITwilioRestClient Create(string accountSid, string apiKeySid, string apiKeySecret)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        return new TwilioRestClient(
            username: apiKeySid,
            password: apiKeySecret,
            accountSid: accountSid,
            httpClient: new Twilio.Http.SystemNetHttpClient(http));
    }
}

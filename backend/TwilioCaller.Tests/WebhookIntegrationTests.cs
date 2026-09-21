using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services;
using Xunit;

namespace TwilioCaller.Tests;

/// <summary>
/// Boots the real API in-process against a throwaway SQLite database. These cover the
/// webhook handlers, which are the parts Twilio drives and that no manual test of the
/// app would exercise without a live phone call.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PublicBaseUrl = "https://caller.test";
    public const string AuthToken = "an-account-auth-token";

    private SqliteConnection _sqlite = null!;

    public string ConnectionId { get; } = "conn1234abcd";
    public string WebhookKey { get; } = "webhook-key-value";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["PublicBaseUrl"] = PublicBaseUrl,
                ["Security:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Security:SessionSigningKey"] = "a-test-signing-key-long-enough-for-hmac-256",
            }));

        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            services.Remove(descriptor);

            _sqlite = new SqliteConnection("Filename=:memory:");
            _sqlite.Open();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_sqlite));
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();

        db.Connections.Add(new TwilioConnection
        {
            Id = ConnectionId,
            AccountSid = "ACtest",
            ApiKeySid = "SKtest",
            ApiKeySecretCipher = protector.Protect("api-key-secret"),
            WebhookKey = WebhookKey,
            FriendlyName = "Test account",
            TwimlAppSid = "APtest",
        });

        await db.SaveChangesAsync();
    }

    public async Task<AppDbContext> NewDbContextAsync()
    {
        var scope = Services.CreateScope();
        return await Task.FromResult(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task AddDeviceAsync(string identity, bool supportsVoip)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Devices.Add(new DeviceRegistration
        {
            ConnectionId = ConnectionId,
            Identity = identity,
            Platform = "android",
            SupportsVoip = supportsVoip,
        });
        await db.SaveChangesAsync();
    }

    public async Task SetFallbackAsync(string? number)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.Connections.FirstAsync(c => c.Id == ConnectionId);
        connection.FallbackForwardNumber = number;
        await db.SaveChangesAsync();
    }

    public async Task EnableSignatureValidationAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
        var connection = await db.Connections.FirstAsync(c => c.Id == ConnectionId);
        connection.AuthTokenCipher = protector.Protect(AuthToken);
        await db.SaveChangesAsync();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        _sqlite?.Dispose();
        return Task.CompletedTask;
    }
}

public class WebhookTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private string VoicePath => $"/webhooks/{factory.ConnectionId}/{factory.WebhookKey}/voice";
    private string SmsPath => $"/webhooks/{factory.ConnectionId}/{factory.WebhookKey}/sms";

    [Fact]
    public async Task Outbound_Call_From_The_App_Dials_The_Destination_With_The_Chosen_Caller_Id()
    {
        var response = await PostAsync(VoicePath, new()
        {
            ["From"] = "client:android_abc123",
            ["To"] = "+15558675309",
            ["CallerId"] = "+15550001111",
            ["CallSid"] = "CA_outbound_1",
        });

        var twiml = await ReadTwimlAsync(response);

        Assert.Contains("callerId=\"+15550001111\"", twiml);
        Assert.Contains("<Number>+15558675309</Number>", twiml);
        Assert.DoesNotContain("<Client>", twiml);
    }

    [Fact]
    public async Task Outbound_Call_Without_A_Destination_Does_Not_Dial()
    {
        var response = await PostAsync(VoicePath, new()
        {
            ["From"] = "client:android_abc123",
            ["CallSid"] = "CA_outbound_2",
        });

        var twiml = await ReadTwimlAsync(response);

        Assert.DoesNotContain("<Dial", twiml);
        Assert.Contains("<Say>", twiml);
    }

    [Fact]
    public async Task Inbound_Call_Rings_Every_Registered_Voip_Client()
    {
        await factory.AddDeviceAsync("android_ring1", supportsVoip: true);
        await factory.AddDeviceAsync("ios_ring2", supportsVoip: true);
        await factory.AddDeviceAsync("web_noring", supportsVoip: false);

        var response = await PostAsync(VoicePath, new()
        {
            ["From"] = "+15551234567",
            ["To"] = "+15550001111",
            ["CallSid"] = "CA_inbound_1",
            ["CallStatus"] = "ringing",
        });

        var twiml = await ReadTwimlAsync(response);

        Assert.Contains("<Client>android_ring1</Client>", twiml);
        Assert.Contains("<Client>ios_ring2</Client>", twiml);
        // A device that cannot run the Voice SDK would never answer.
        Assert.DoesNotContain("web_noring", twiml);
    }

    [Fact]
    public async Task Inbound_Call_Falls_Back_To_A_Real_Handset_When_No_Client_Is_Registered()
    {
        await using var isolated = new ApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        await isolated.SetFallbackAsync("+15559998888");

        var response = await isolated.CreateClient().PostAsync(
            $"/webhooks/{isolated.ConnectionId}/{isolated.WebhookKey}/voice",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = "+15551234567",
                ["To"] = "+15550001111",
                ["CallSid"] = "CA_inbound_fallback",
                ["CallStatus"] = "ringing",
            }));

        var twiml = await response.Content.ReadAsStringAsync();

        Assert.Contains("<Number>+15559998888</Number>", twiml);
    }

    [Fact]
    public async Task Inbound_Call_With_Nowhere_To_Go_Is_Answered_Politely()
    {
        await using var isolated = new ApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();

        var response = await isolated.CreateClient().PostAsync(
            $"/webhooks/{isolated.ConnectionId}/{isolated.WebhookKey}/voice",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = "+15551234567",
                ["To"] = "+15550001111",
                ["CallSid"] = "CA_inbound_nobody",
                ["CallStatus"] = "ringing",
            }));

        var twiml = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<Dial", twiml);
        Assert.Contains("<Say>", twiml);
    }

    [Fact]
    public async Task Inbound_Sms_Is_Stored_And_Acknowledged_Without_An_Auto_Reply()
    {
        var response = await PostAsync(SmsPath, new()
        {
            ["MessageSid"] = "SM_inbound_1",
            ["From"] = "+15551234567",
            ["To"] = "+15550001111",
            ["Body"] = "hello from the outside",
            ["NumMedia"] = "0",
        });

        var twiml = await ReadTwimlAsync(response);
        Assert.DoesNotContain("<Message>", twiml);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.InboundEvents.SingleAsync(e => e.TwilioSid == "SM_inbound_1");

        Assert.Equal("message", stored.Kind);
        Assert.Equal("hello from the outside", stored.Body);
        Assert.Equal("+15551234567", stored.FromNumber);
    }

    [Fact]
    public async Task A_Retried_Webhook_Does_Not_Duplicate_The_Event()
    {
        var form = new Dictionary<string, string>
        {
            ["MessageSid"] = "SM_retry_1",
            ["From"] = "+15551234567",
            ["To"] = "+15550001111",
            ["Body"] = "twilio retries on timeout",
            ["NumMedia"] = "0",
        };

        await PostAsync(SmsPath, form);
        await PostAsync(SmsPath, form);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await db.InboundEvents.CountAsync(e => e.TwilioSid == "SM_retry_1"));
    }

    [Fact]
    public async Task A_Wrong_Webhook_Key_Is_Refused()
    {
        var response = await factory.CreateClient().PostAsync(
            $"/webhooks/{factory.ConnectionId}/not-the-right-key/sms",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MessageSid"] = "SM_bad_key",
                ["From"] = "+1555",
                ["To"] = "+1555",
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_Unknown_Connection_Is_Not_Found()
    {
        var response = await factory.CreateClient().PostAsync(
            "/webhooks/nosuchconnection/anykey/sms",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["MessageSid"] = "SM_x" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task When_An_Auth_Token_Is_On_File_An_Unsigned_Request_Is_Refused()
    {
        await using var isolated = new ApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        await isolated.EnableSignatureValidationAsync();

        var response = await isolated.CreateClient().PostAsync(
            $"/webhooks/{isolated.ConnectionId}/{isolated.WebhookKey}/sms",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MessageSid"] = "SM_unsigned",
                ["From"] = "+1555",
                ["To"] = "+1555",
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task When_An_Auth_Token_Is_On_File_A_Correctly_Signed_Request_Is_Accepted()
    {
        await using var isolated = new ApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        await isolated.EnableSignatureValidationAsync();

        var path = $"/webhooks/{isolated.ConnectionId}/{isolated.WebhookKey}/sms";
        var form = new Dictionary<string, string>
        {
            ["MessageSid"] = "SM_signed",
            ["From"] = "+15551234567",
            ["To"] = "+15550001111",
            ["Body"] = "signed",
        };

        var client = isolated.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiFactory.PublicBaseUrl),
        });

        // Sign exactly as Twilio would, over the URL the app will see after
        // forwarded-header processing.
        var signature = TwilioSignature.Compute(
            ApiFactory.AuthToken, $"{ApiFactory.PublicBaseUrl}{path}", form);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add("X-Twilio-Signature", signature);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form) =>
        factory.CreateClient().PostAsync(path, new FormUrlEncodedContent(form));

    private static async Task<string> ReadTwimlAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}


/// <summary>
/// Reimplements Twilio's request-signing algorithm so a test can produce a signature
/// the server will accept: HMAC-SHA1, over the full URL followed by every POST
/// parameter sorted by name and concatenated as name then value.
/// </summary>
internal static class TwilioSignature
{
    public static string Compute(string authToken, string url, IDictionary<string, string> form)
    {
        var payload = new System.Text.StringBuilder(url);
        foreach (var pair in form.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            payload.Append(pair.Key).Append(pair.Value);
        }

        using var hmac = new HMACSHA1(System.Text.Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload.ToString())));
    }
}

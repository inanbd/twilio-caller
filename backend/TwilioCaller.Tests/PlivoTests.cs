using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services;
using TwilioCaller.Api.Services.Plivo;
using Xunit;

namespace TwilioCaller.Tests;

/// <summary>
/// Stands in for api.plivo.com: records every request the backend makes and answers
/// the handful of resources it uses in Plivo's documented shapes.
/// </summary>
public class FakePlivoApi : HttpMessageHandler
{
    public const string AuthId = "MATESTAUTHID12345678";
    public const string AuthToken = "an-account-auth-token";
    public const string AppId = "77001122334455";

    public record Call(string Method, string Path, string Query, JsonElement Body);

    public ConcurrentQueue<Call> Calls { get; } = new();

    /// <summary>Number (digits) → the application URI Plivo reports for it.</summary>
    public ConcurrentDictionary<string, string?> Numbers { get; } = new()
    {
        ["14155550000"] = null,
        ["14155550001"] = "/v1/Account/" + AuthId + "/Application/999/",
    };

    private int _endpoints;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AuthId}:{AuthToken}"));
        if (request.Headers.Authorization?.Parameter != expected)
        {
            return Json(HttpStatusCode.Unauthorized, new { error = "Could not verify your access level." });
        }

        var prefix = $"/v1/Account/{AuthId}/";
        var path = request.RequestUri!.AbsolutePath[prefix.Length..];
        var body = request.Content is null
            ? default
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        Calls.Enqueue(new Call(request.Method.Method, path, request.RequestUri.Query, body));

        return (request.Method.Method, path) switch
        {
            ("GET", "") => Json(HttpStatusCode.OK, new { auth_id = AuthId, name = "Plivo Test Co" }),

            ("POST", "Application/") =>
                Json(HttpStatusCode.Created, new { message = "created", app_id = AppId }),
            ("POST", var p) when p == $"Application/{AppId}/" =>
                Json(HttpStatusCode.Accepted, new { message = "changed" }),

            ("GET", "Number/") => Json(HttpStatusCode.OK, new
            {
                meta = new { limit = 20, offset = 0, total_count = Numbers.Count },
                objects = Numbers.Select(n => new
                {
                    number = n.Key,
                    alias = n.Key == "14155550000" ? "Main line" : "",
                    application = n.Value,
                    sms_enabled = true,
                    voice_enabled = true,
                    mms_enabled = false,
                }),
            }),
            ("POST", var p) when p.StartsWith("Number/") => AttachNumber(p, body),

            ("POST", "Message/") => Json(HttpStatusCode.Accepted, new
            {
                message = "message(s) queued",
                message_uuid = new[] { "msg-uuid-1" },
            }),

            ("POST", "Call/") =>
                Json(HttpStatusCode.Created, new { message = "call fired", request_uuid = "req-uuid-1" }),
            ("GET", "Call/") => Json(HttpStatusCode.OK, new
            {
                objects = new[]
                {
                    new
                    {
                        call_uuid = "call-1",
                        from_number = "14155559999",
                        to_number = "14155550000",
                        call_direction = "inbound",
                        call_duration = 42,
                        call_state = "ANSWER",
                        initiation_time = "2026-03-20 23:47:24+05:30",
                    },
                },
            }),

            ("POST", "Endpoint/") => Json(HttpStatusCode.Created, new
            {
                message = "created",
                // Plivo appends digits of its own to the requested username.
                username = body.GetProperty("username").GetString() + "1234567890",
                endpoint_id = $"ep-{Interlocked.Increment(ref _endpoints)}",
            }),

            _ => Json(HttpStatusCode.NotFound, new { error = "not found" }),
        };
    }

    private HttpResponseMessage AttachNumber(string path, JsonElement body)
    {
        var number = path["Number/".Length..].TrimEnd('/');
        if (!Numbers.ContainsKey(number))
        {
            return Json(HttpStatusCode.NotFound, new { error = "not found" });
        }

        Numbers[number] = $"/v1/Account/{AuthId}/Application/{body.GetProperty("app_id").GetString()}/";
        return Json(HttpStatusCode.Accepted, new { message = "changed" });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status) { Content = JsonContent.Create(value) };
}

/// <summary>The real API in-process, with a Plivo connection seeded and Plivo itself faked.</summary>
public class PlivoApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PublicBaseUrl = "https://caller.test";

    private SqliteConnection _sqlite = null!;

    public FakePlivoApi Plivo { get; } = new();
    public string ConnectionId { get; } = "plivoconn1234";
    public string WebhookKey { get; } = "plivo-webhook-key";
    public string Root => $"/webhooks/{ConnectionId}/{WebhookKey}/plivo";

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

            services.AddHttpClient(PlivoClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Plivo);
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
            Provider = Providers.Plivo,
            AccountSid = FakePlivoApi.AuthId,
            AuthTokenCipher = protector.Protect(FakePlivoApi.AuthToken),
            WebhookKey = WebhookKey,
            FriendlyName = "Plivo Test Co",
            TwimlAppSid = FakePlivoApi.AppId,
        });

        await db.SaveChangesAsync();
    }

    public async Task<string> AddDeviceAsync(
        string identity, bool supportsVoip, string? endpointUsername = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Devices.Add(new DeviceRegistration
        {
            ConnectionId = ConnectionId,
            Identity = identity,
            Platform = supportsVoip ? "web" : "android",
            SupportsVoip = supportsVoip,
            PlivoEndpointUsername = endpointUsername,
        });
        await db.SaveChangesAsync();

        return scope.ServiceProvider.GetRequiredService<SessionTokenService>()
            .Issue(ConnectionId, identity).Token;
    }

    /// <summary>An app client: authenticated with a session token for the seeded connection.</summary>
    public async Task<HttpClient> AppClientAsync(string identity = "web_app", bool supportsVoip = true)
    {
        var token = await AddDeviceAsync(identity, supportsVoip);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Posts a webhook the way Plivo does: form-encoded and V3-signed.</summary>
    public Task<HttpResponseMessage> PlivoPostAsync(
        string path, Dictionary<string, string> form, string? authToken = FakePlivoApi.AuthToken)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(PublicBaseUrl),
        });

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form),
        };

        if (authToken is not null)
        {
            const string nonce = "05429567804466091622";
            var parameters = form.Select(f =>
                new KeyValuePair<string, IReadOnlyList<string>>(f.Key, new[] { f.Value }));
            request.Headers.Add(PlivoSignature.NonceHeader, nonce);
            request.Headers.Add(PlivoSignature.SignatureHeader, PlivoSignature.Compute(
                "POST", $"{PublicBaseUrl}{path}", parameters, nonce, authToken));
        }

        return client.SendAsync(request);
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        _sqlite?.Dispose();
        return Task.CompletedTask;
    }
}

public class PlivoSignatureTests
{
    private const string Token = "an-account-auth-token";

    private static KeyValuePair<string, IReadOnlyList<string>>[] Form(params (string, string)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, IReadOnlyList<string>>(p.Item1, new[] { p.Item2 }))
            .ToArray();

    // The expected values are HMAC-SHA256 (computed with openssl) over the base
    // strings Plivo's SDKs build for these inputs, so they pin the canonical form
    // itself rather than merely agreeing with our own implementation.

    [Fact]
    public void Signs_A_Post_As_Url_Then_Sorted_Parameters_Then_Nonce()
    {
        // https://…/voice?CallStatusringingCallUUIDabc-123From14155551234To14155550000.12345
        var signature = PlivoSignature.Compute(
            "POST",
            "https://caller.test/webhooks/c1/k1/plivo/voice",
            Form(("From", "14155551234"), ("To", "14155550000"),
                ("CallUUID", "abc-123"), ("CallStatus", "ringing")),
            "12345",
            Token);

        Assert.Equal("efPpFWJ6PoI0ejpLLZ+8OIcI5W1zQ8yNXBG9dmy6Wz8=", signature);
    }

    [Fact]
    public void Signs_A_Query_String_Sorted_And_Separated_From_The_Body_By_A_Dot()
    {
        // https://…/sms?a=1&b=2.From14155551234Texthello there.nonce-xyz
        var signature = PlivoSignature.Compute(
            "POST",
            "https://caller.test/webhooks/c1/k1/plivo/sms?b=2&a=1",
            Form(("Text", "hello there"), ("From", "14155551234")),
            "nonce-xyz",
            Token);

        Assert.Equal("uF86oXCEXpV3YMAV8ohAh5Fb9yirF/hgAObh3nIvE8k=", signature);
    }

    [Fact]
    public void Signs_A_Post_Without_Parameters_As_Just_Url_And_Nonce()
    {
        // https://…/voice/status.n0
        var signature = PlivoSignature.Compute(
            "POST", "https://caller.test/webhooks/c1/k1/plivo/voice/status", Form(), "n0", Token);

        Assert.Equal("FqVM+OxVO9g47ijmof6dSIyACspyhYwB04EyBPCQZB0=", signature);
    }

    [Fact]
    public void V2_Signs_The_Url_Without_Its_Query_Followed_By_The_Nonce()
    {
        // https://caller.test/webhooks/c1/k1/plivo/sms/statusabc123
        Assert.Equal(
            "9jz/svKfSGZcx6CgH3Kj3BChWlnxQ7vec2vYzk3Y6/c=",
            PlivoSignature.ComputeV2("https://caller.test/webhooks/c1/k1/plivo/sms/status?x=1", "abc123", Token));
    }

    [Fact]
    public void Validate_Accepts_Any_One_Of_Several_Signatures_And_Rejects_The_Rest()
    {
        const string url = "https://caller.test/webhooks/c1/k1/plivo/voice/status";
        const string good = "FqVM+OxVO9g47ijmof6dSIyACspyhYwB04EyBPCQZB0=";

        Assert.True(PlivoSignature.Validate("POST", url, Form(), "n0", Token, $"bogus,{good}"));
        Assert.False(PlivoSignature.Validate("POST", url, Form(), "n0", "another-token", good));
        Assert.False(PlivoSignature.Validate("POST", url, Form(), "other-nonce", Token, good));
        Assert.False(PlivoSignature.Validate("POST", url, Form(), "n0", Token, string.Empty));
        Assert.False(PlivoSignature.Validate("POST", url, Form(), string.Empty, Token, good));
    }
}

public class PlivoNumberTests
{
    [Theory]
    [InlineData("14155551234", "+14155551234")]
    [InlineData("+14155551234", "+14155551234")]
    [InlineData("54321", "54321")] // a short code is not E.164
    [InlineData("ACMEBANK", "ACMEBANK")] // nor is an alphanumeric sender
    [InlineData("", "")]
    public void FromPlivo_Adds_A_Plus_Only_To_Real_Numbers(string plivo, string expected) =>
        Assert.Equal(expected, PlivoNumbers.FromPlivo(plivo));

    [Fact]
    public void SipUser_Unwraps_A_Sip_Uri()
    {
        Assert.Equal("alice123", PlivoNumbers.SipUser("sip:alice123@phone.plivo.com"));
        Assert.Equal("14155551234", PlivoNumbers.SipUser("14155551234"));
    }

    [Theory]
    [InlineData("ANSWER", null, 12, "completed")]
    [InlineData("BUSY", null, 0, "busy")]
    [InlineData("NO_ANSWER", null, 0, "no-answer")]
    [InlineData(null, "Normal Hangup", 30, "completed")]
    [InlineData(null, "Busy Line", 0, "busy")]
    [InlineData(null, null, null, "unknown")]
    public void Call_States_Fold_Onto_The_Statuses_The_App_Knows(
        string? state, string? cause, int? duration, string expected) =>
        Assert.Equal(expected, PlivoApiService.CallStatus(state, cause, duration));
}

public class PlivoWebhookTests(PlivoApiFactory factory) : IClassFixture<PlivoApiFactory>
{
    [Fact]
    public async Task Unsigned_Or_Wrongly_Signed_Webhooks_Are_Refused()
    {
        var form = new Dictionary<string, string>
        {
            ["From"] = "14155559999", ["To"] = "14155550000", ["CallUUID"] = "call-unsigned",
        };

        var unsigned = await factory.PlivoPostAsync($"{factory.Root}/voice", form, authToken: null);
        var forged = await factory.PlivoPostAsync($"{factory.Root}/voice", form, authToken: "guess");

        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
    }

    [Fact]
    public async Task A_Messaging_Callback_Signed_The_Older_V2_Way_Is_Accepted()
    {
        var path = $"{factory.Root}/sms/status";
        var form = new Dictionary<string, string> { ["MessageUUID"] = "m-v2", ["Status"] = "sent" };

        async Task<HttpStatusCode> PostAsync(string token, bool withBadV3)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri(PlivoApiFactory.PublicBaseUrl),
            });
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Add(PlivoSignature.V2NonceHeader, "v2-nonce");
            request.Headers.Add(PlivoSignature.V2SignatureHeader,
                PlivoSignature.ComputeV2($"{PlivoApiFactory.PublicBaseUrl}{path}", "v2-nonce", token));
            if (withBadV3)
            {
                request.Headers.Add(PlivoSignature.NonceHeader, "n");
                request.Headers.Add(PlivoSignature.SignatureHeader, "forged");
            }

            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await PostAsync(FakePlivoApi.AuthToken, withBadV3: false));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("guess", withBadV3: false));
        // A valid V2 signature must not rescue a request whose V3 signature is wrong.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(FakePlivoApi.AuthToken, withBadV3: true));
    }

    [Fact]
    public async Task A_Wrong_Webhook_Key_Is_Refused()
    {
        var response = await factory.PlivoPostAsync(
            $"/webhooks/{factory.ConnectionId}/not-the-key/plivo/sms",
            new() { ["From"] = "14155559999", ["To"] = "14155550000", ["MessageUUID"] = "m-x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Twilio_Routes_Do_Not_Serve_A_Plivo_Connection()
    {
        var response = await factory.CreateClient().PostAsync(
            $"/webhooks/{factory.ConnectionId}/{factory.WebhookKey}/voice",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["From"] = "+15551234567" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Outbound_Call_From_The_Browser_Sdk_Dials_The_Destination_With_The_Chosen_Caller_Id()
    {
        var response = await factory.PlivoPostAsync($"{factory.Root}/voice", new()
        {
            ["From"] = "sip:caller9f3k2a1234567890@phone.plivo.com",
            ["To"] = "sip:14155557777@phone.plivo.com",
            ["CallUUID"] = "call-outbound",
            ["X-PH-CallerId"] = "14155550000",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "<Response><Dial callerId=\"14155550000\"><Number>14155557777</Number></Dial></Response>",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Outbound_Call_Without_A_Caller_Id_Is_Not_Dialled()
    {
        var response = await factory.PlivoPostAsync($"{factory.Root}/voice", new()
        {
            ["From"] = "sip:caller9f3k2a1234567890@phone.plivo.com",
            ["To"] = "14155557777",
            ["CallUUID"] = "call-outbound-no-id",
        });

        var xml = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<Dial", xml);
        Assert.Contains("<Speak>", xml);
    }

    [Fact]
    public async Task Inbound_Call_Rings_Only_Devices_With_A_Registered_Endpoint()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        await isolated.AddDeviceAsync("web_ringing", supportsVoip: true, endpointUsername: "callerabc1234567890");
        await isolated.AddDeviceAsync("web_never_registered", supportsVoip: true);
        await isolated.AddDeviceAsync("android_dialout", supportsVoip: false);

        var response = await isolated.PlivoPostAsync($"{isolated.Root}/voice", new()
        {
            ["From"] = "14155559999",
            ["To"] = "14155550000",
            ["CallUUID"] = "call-inbound",
            ["CallStatus"] = "ringing",
        });

        Assert.Equal(
            "<Response><Dial callerId=\"14155559999\" timeout=\"30\">" +
            "<User>sip:callerabc1234567890@phone.plivo.com</User></Dial></Response>",
            await response.Content.ReadAsStringAsync());

        using var scope = isolated.Services.CreateScope();
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .InboundEvents.SingleAsync(e => e.TwilioSid == "call-inbound");
        Assert.Equal("+14155559999", evt.FromNumber);
        Assert.Equal("+14155550000", evt.ToNumber);
    }

    [Fact]
    public async Task Inbound_Call_Falls_Back_To_The_Handset_Then_To_A_Polite_Message()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        var form = new Dictionary<string, string>
        {
            ["From"] = "14155559999", ["To"] = "14155550000", ["CallUUID"] = "call-fallback",
        };

        var nobody = await (await isolated.PlivoPostAsync($"{isolated.Root}/voice", form))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("<Dial", nobody);
        Assert.Contains("<Speak>", nobody);

        using (var scope = isolated.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Connections.SingleAsync()).FallbackForwardNumber = "+14155558888";
            await db.SaveChangesAsync();
        }

        var forwarded = await (await isolated.PlivoPostAsync($"{isolated.Root}/voice", form))
            .Content.ReadAsStringAsync();
        Assert.Equal(
            "<Response><Dial callerId=\"14155550000\"><Number>14155558888</Number></Dial></Response>",
            forwarded);
    }

    [Fact]
    public async Task Dial_Out_Bridge_Connects_The_Handset_To_The_Destination()
    {
        var response = await factory.PlivoPostAsync(
            $"{factory.Root}/bridge/14155557777/14155550000",
            new() { ["CallUUID"] = "call-bridge", ["Event"] = "StartApp" });

        Assert.Equal(
            "<Response><Speak>Connecting you now.</Speak>" +
            "<Dial callerId=\"14155550000\"><Number>14155557777</Number></Dial></Response>",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Inbound_Sms_Is_Kept_Because_Plivo_Will_Not_Serve_Its_Text_Later()
    {
        var app = await factory.AppClientAsync("web_inbox");
        var form = new Dictionary<string, string>
        {
            ["From"] = "14155559999",
            ["To"] = "14155550000",
            ["Text"] = "hello from the outside",
            ["Type"] = "sms",
            ["MessageUUID"] = "msg-inbound-1",
        };

        var response = await factory.PlivoPostAsync($"{factory.Root}/sms", form);
        // Plivo retries; the duplicate must not produce a second message.
        await factory.PlivoPostAsync($"{factory.Root}/sms", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<Response />", await response.Content.ReadAsStringAsync());

        var thread = await app.GetFromJsonAsync<JsonElement>(
            "/api/messages?number=%2B14155550000&peer=%2B14155559999");
        var message = Assert.Single(thread.EnumerateArray());
        Assert.Equal("hello from the outside", message.GetProperty("body").GetString());
        Assert.Equal("inbound", message.GetProperty("direction").GetString());
        Assert.Equal("+14155559999", message.GetProperty("from").GetString());

        var events = await app.GetFromJsonAsync<JsonElement>("/api/events");
        Assert.Contains(events.EnumerateArray(),
            e => e.GetProperty("sid").GetString() == "msg-inbound-1" &&
                 e.GetProperty("kind").GetString() == "message");
    }
}

public class PlivoApiTests(PlivoApiFactory factory) : IClassFixture<PlivoApiFactory>
{
    [Fact]
    public async Task Connect_Verifies_The_Credentials_And_Provisions_An_Application()
    {
        await using var isolated = new PlivoApiFactory();

        var response = await isolated.CreateClient().PostAsJsonAsync("/api/auth/connect", new
        {
            provider = "plivo",
            accountSid = "MANEWACCOUNT00000000",
            authToken = FakePlivoApi.AuthToken,
            platform = "web",
            supportsVoip = true,
        });

        // The fake only knows one account, so a different Auth ID is a rejection —
        // which must surface as "bad credentials", not as a server error.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("plivo_rejected_credentials",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        response = await isolated.CreateClient().PostAsJsonAsync("/api/auth/connect", new
        {
            provider = "plivo",
            accountSid = FakePlivoApi.AuthId,
            authToken = FakePlivoApi.AuthToken,
            platform = "web",
            supportsVoip = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plivo", body.GetProperty("provider").GetString());
        Assert.Equal("Plivo Test Co", body.GetProperty("friendlyName").GetString());
        Assert.True(body.GetProperty("voiceReady").GetBoolean());

        var created = isolated.Plivo.Calls.Single(c => c is { Method: "POST", Path: "Application/" });
        var connectionId = body.GetProperty("connectionId").GetString();
        Assert.StartsWith(
            $"{PlivoApiFactory.PublicBaseUrl}/webhooks/{connectionId}/",
            created.Body.GetProperty("answer_url").GetString());
        Assert.EndsWith("/plivo/voice", created.Body.GetProperty("answer_url").GetString());
        Assert.EndsWith("/plivo/sms", created.Body.GetProperty("message_url").GetString());
        Assert.EndsWith("/plivo/voice/status", created.Body.GetProperty("hangup_url").GetString());

        using var scope = isolated.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Connections.SingleAsync(c => c.Id == connectionId);
        Assert.Equal(Providers.Plivo, connection.Provider);
        Assert.DoesNotContain(FakePlivoApi.AuthToken, connection.AuthTokenCipher);
    }

    [Theory]
    [InlineData("plivo", "ACnotplivo", "token", "bad_auth_id")]
    [InlineData("plivo", "MAOK", "", "missing_credentials")]
    [InlineData("vonage", "MAOK", "token", "unknown_provider")]
    public async Task Connect_Rejects_Malformed_Requests(
        string provider, string accountSid, string authToken, string expectedError)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/connect", new
        {
            provider, accountSid, authToken, platform = "web", supportsVoip = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedError,
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Session_Reports_The_Provider()
    {
        var app = await factory.AppClientAsync("web_session");

        var session = await app.GetFromJsonAsync<JsonElement>("/api/auth/session");

        Assert.Equal("plivo", session.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Numbers_Are_Listed_In_E164_And_Provisioning_Attaches_Them_To_Our_Application()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        var app = await isolated.AppClientAsync();

        var before = await app.GetFromJsonAsync<JsonElement>("/api/numbers");
        Assert.Equal(2, before.GetArrayLength());
        Assert.Equal("+14155550000", before[0].GetProperty("phoneNumber").GetString());
        Assert.Equal("Main line", before[0].GetProperty("friendlyName").GetString());
        // One is unattached, the other belongs to somebody else's application.
        Assert.All(before.EnumerateArray(),
            n => Assert.False(n.GetProperty("wiredToThisBackend").GetBoolean()));

        var provision = await app.PostAsJsonAsync("/api/numbers/provision", new
        {
            phoneNumberSids = new[] { before[0].GetProperty("sid").GetString(), "19990000000" },
            fallbackForwardNumber = "+14155558888",
        });
        var result = await provision.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, provision.StatusCode);
        Assert.Equal(FakePlivoApi.AppId, result.GetProperty("twimlAppSid").GetString());
        Assert.Single(result.GetProperty("updatedNumberSids").EnumerateArray());
        Assert.Single(result.GetProperty("failures").EnumerateArray());

        var after = await app.GetFromJsonAsync<JsonElement>("/api/numbers");
        Assert.True(after[0].GetProperty("wiredToThisBackend").GetBoolean());
        Assert.False(after[1].GetProperty("wiredToThisBackend").GetBoolean());
    }

    [Fact]
    public async Task Sent_Message_Goes_To_Plivo_As_Digits_And_Then_Tracks_Its_Delivery_Report()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        var app = await isolated.AppClientAsync();

        var send = await app.PostAsJsonAsync("/api/messages", new
        {
            from = "+14155550000", to = "+14155557777", body = "on my way",
        });
        var sent = await send.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("msg-uuid-1", sent.GetProperty("sid").GetString());
        Assert.Equal("queued", sent.GetProperty("status").GetString());

        var request = isolated.Plivo.Calls.Single(c => c is { Method: "POST", Path: "Message/" }).Body;
        Assert.Equal("14155550000", request.GetProperty("src").GetString());
        Assert.Equal("14155557777", request.GetProperty("dst").GetString());
        Assert.Equal("on my way", request.GetProperty("text").GetString());
        Assert.Equal($"{PlivoApiFactory.PublicBaseUrl}{isolated.Root}/sms/status",
            request.GetProperty("url").GetString());

        var report = await isolated.PlivoPostAsync($"{isolated.Root}/sms/status", new()
        {
            ["MessageUUID"] = "msg-uuid-1", ["Status"] = "delivered",
            ["From"] = "14155550000", ["To"] = "14155557777",
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        var conversations = await app.GetFromJsonAsync<JsonElement>(
            "/api/messages/conversations?number=%2B14155550000");
        var conversation = Assert.Single(conversations.EnumerateArray());
        Assert.Equal("+14155557777", conversation.GetProperty("peerNumber").GetString());
        Assert.Equal("on my way", conversation.GetProperty("lastBody").GetString());

        var thread = await app.GetFromJsonAsync<JsonElement>(
            "/api/messages?number=%2B14155550000&peer=%2B14155557777");
        Assert.Equal("delivered", Assert.Single(thread.EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Calls_Are_Listed_With_Plivo_Fields_Mapped_Onto_The_App_Contract()
    {
        var app = await factory.AppClientAsync("web_calls");

        var calls = await app.GetFromJsonAsync<JsonElement>("/api/calls?number=%2B14155550000");

        // The same call comes back from both the to- and from- queries; it is one call.
        var call = Assert.Single(calls.EnumerateArray());
        Assert.Equal("call-1", call.GetProperty("sid").GetString());
        Assert.Equal("+14155559999", call.GetProperty("from").GetString());
        Assert.Equal("inbound", call.GetProperty("direction").GetString());
        Assert.Equal("completed", call.GetProperty("status").GetString());
        Assert.Equal(42, call.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(
            DateTimeOffset.Parse("2026-03-20T18:17:24Z"),
            call.GetProperty("startedAt").GetDateTimeOffset());

        Assert.Contains(factory.Plivo.Calls, c => c.Path == "Call/" && c.Query.Contains("to_number=14155550000"));
    }

    [Fact]
    public async Task Dial_Out_Rings_The_Handset_And_Points_Plivo_At_The_Bridge()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        var app = await isolated.AppClientAsync("android_app", supportsVoip: false);

        var response = await app.PostAsJsonAsync("/api/calls/dial-out", new
        {
            from = "+14155550000", to = "+14155557777", bridgeTo = "+14155558888",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = isolated.Plivo.Calls.Single(c => c is { Method: "POST", Path: "Call/" }).Body;
        Assert.Equal("14155550000", request.GetProperty("from").GetString());
        Assert.Equal("14155558888", request.GetProperty("to").GetString());
        Assert.Equal(
            $"{PlivoApiFactory.PublicBaseUrl}{isolated.Root}/bridge/14155557777/14155550000",
            request.GetProperty("answer_url").GetString());
    }

    [Fact]
    public async Task Voice_Credentials_Create_One_Endpoint_Per_Device_And_Then_Reuse_It()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        var app = await isolated.AppClientAsync("web_voice");

        var first = await app.GetFromJsonAsync<JsonElement>("/api/voice/token");
        var second = await app.GetFromJsonAsync<JsonElement>("/api/voice/token");

        Assert.Equal("plivo", first.GetProperty("provider").GetString());
        var username = first.GetProperty("username").GetString()!;
        Assert.EndsWith("1234567890", username); // the name Plivo answered with, not the one asked for
        Assert.Equal(username, second.GetProperty("username").GetString());
        Assert.Equal(first.GetProperty("password").GetString(), second.GetProperty("password").GetString());

        var created = Assert.Single(isolated.Plivo.Calls, c => c is { Method: "POST", Path: "Endpoint/" });
        Assert.Equal(FakePlivoApi.AppId, created.Body.GetProperty("app_id").GetString());
        Assert.Equal(first.GetProperty("password").GetString(), created.Body.GetProperty("password").GetString());

        // ...and that endpoint is what an inbound call now rings.
        var inbound = await isolated.PlivoPostAsync($"{isolated.Root}/voice", new()
        {
            ["From"] = "14155559999", ["To"] = "14155550000", ["CallUUID"] = "call-after-register",
        });
        Assert.Contains($"<User>sip:{username}@phone.plivo.com</User>",
            await inbound.Content.ReadAsStringAsync());

        using var scope = isolated.Services.CreateScope();
        var device = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Devices.SingleAsync(d => d.Identity == "web_voice");
        Assert.DoesNotContain(first.GetProperty("password").GetString()!, device.PlivoEndpointPasswordCipher);
    }

    [Fact]
    public async Task Voice_Credentials_Are_Refused_To_A_Device_That_Cannot_Run_The_Browser_Sdk()
    {
        var app = await factory.AppClientAsync("android_no_voip", supportsVoip: false);

        var response = await app.GetAsync("/api/voice/token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Plivo.Calls, c => c.Path == "Endpoint/");
    }

    [Fact]
    public async Task A_Plivo_Failure_Is_Reported_Without_Looking_Like_An_Expired_Session()
    {
        await using var isolated = new PlivoApiFactory();
        await ((IAsyncLifetime)isolated).InitializeAsync();
        var app = await isolated.AppClientAsync();

        // The stored token no longer works, as after the user rotates it at Plivo.
        using (var scope = isolated.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
            (await db.Connections.SingleAsync()).AuthTokenCipher = protector.Protect("rotated-away");
            await db.SaveChangesAsync();
        }

        var response = await app.GetAsync("/api/numbers");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("plivo_error",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}

public class SchemaUpgraderTests
{
    [Fact]
    public async Task A_Database_From_Before_Plivo_Support_Is_Upgraded_In_Place()
    {
        await using var sqlite = new SqliteConnection("Filename=:memory:");
        await sqlite.OpenAsync();

        // The schema exactly as the Twilio-only release created it, with a row in it.
        foreach (var sql in new[]
                 {
                     """
                     CREATE TABLE "Connections" (
                         "Id" TEXT NOT NULL CONSTRAINT "PK_Connections" PRIMARY KEY,
                         "AccountSid" TEXT NOT NULL, "ApiKeySid" TEXT NOT NULL,
                         "ApiKeySecretCipher" TEXT NOT NULL, "AuthTokenCipher" TEXT NULL,
                         "WebhookKey" TEXT NOT NULL, "FriendlyName" TEXT NOT NULL,
                         "TwimlAppSid" TEXT NULL, "FallbackForwardNumber" TEXT NULL,
                         "AndroidPushCredentialSid" TEXT NULL, "ApplePushCredentialSid" TEXT NULL,
                         "CreatedAt" INTEGER NOT NULL, "LastSeenAt" INTEGER NOT NULL)
                     """,
                     """
                     CREATE TABLE "Devices" (
                         "Id" TEXT NOT NULL CONSTRAINT "PK_Devices" PRIMARY KEY,
                         "ConnectionId" TEXT NOT NULL, "Identity" TEXT NOT NULL,
                         "Platform" TEXT NOT NULL, "SupportsVoip" INTEGER NOT NULL,
                         "LastSeenAt" INTEGER NOT NULL)
                     """,
                     """
                     CREATE TABLE "InboundEvents" (
                         "Id" INTEGER NOT NULL CONSTRAINT "PK_InboundEvents" PRIMARY KEY AUTOINCREMENT,
                         "ConnectionId" TEXT NOT NULL, "Kind" TEXT NOT NULL, "TwilioSid" TEXT NOT NULL,
                         "FromNumber" TEXT NOT NULL, "ToNumber" TEXT NOT NULL, "Body" TEXT NULL,
                         "Status" TEXT NULL, "NumMedia" INTEGER NOT NULL, "ReceivedAt" INTEGER NOT NULL)
                     """,
                     """
                     INSERT INTO "Connections" VALUES
                         ('old1', 'ACold', 'SKold', 'cipher', NULL, 'key', 'Old account',
                          'APold', NULL, NULL, NULL, 0, 0)
                     """,
                     """INSERT INTO "Devices" VALUES ('d1', 'old1', 'android_x', 'android', 1, 0)""",
                 })
        {
            await using var command = sqlite.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(sqlite).Options;
        await using var db = new AppDbContext(options);

        SchemaUpgrader.Upgrade(db);
        SchemaUpgrader.Upgrade(db); // every start runs it; the second time must be a no-op

        var connection = await db.Connections.Include(c => c.Devices).SingleAsync();
        Assert.Equal(Providers.Twilio, connection.Provider);
        Assert.Equal("APold", connection.TwimlAppSid);
        Assert.Null(Assert.Single(connection.Devices).PlivoEndpointUsername);

        db.StoredMessages.Add(new StoredMessage { ConnectionId = "old1", Sid = "m1", Body = "hi" });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.StoredMessages.CountAsync());
    }
}

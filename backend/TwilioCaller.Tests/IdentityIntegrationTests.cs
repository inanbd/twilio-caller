using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TwilioCaller.Tests;

/// <summary>
/// Exercises registration, login, session revocation and the admin surface through
/// the real HTTP pipeline, the same way the app and the portal use them.
/// </summary>
public class IdentityIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- registration & login ----------

    [Fact]
    public async Task The_First_Registered_User_Becomes_The_Administrator()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();

        var first = await RegisterAsync(client, "first@example.test");
        var second = await RegisterAsync(client, "second@example.test");

        Assert.Contains("Administrator", first.Roles);
        Assert.DoesNotContain("Administrator", second.Roles);
        Assert.NotEqual(first.Identity, second.Identity);
    }

    [Fact]
    public async Task Registering_The_Same_Email_Twice_Is_Refused()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();

        await RegisterAsync(client, "dupe@example.test");
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "dupe@example.test",
            password = "a-password-8",
            platform = "test",
            supportsVoip = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_With_The_Wrong_Password_Is_Refused()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        await RegisterAsync(client, "user@example.test");

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "user@example.test",
            password = "not-the-password",
            platform = "test",
            supportsVoip = false,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_Returns_A_Token_That_The_Session_Endpoint_Accepts()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        await RegisterAsync(client, "user@example.test");

        var login = await PostAsync<AuthPayload>(client, "/api/auth/login", new
        {
            email = "user@example.test",
            password = Password,
            platform = "test",
            supportsVoip = false,
        });

        var session = await GetAsync<SessionPayload>(client, "/api/auth/session", login.SessionToken);

        Assert.Equal("user@example.test", session.Email);
        Assert.Null(session.Connection);
    }

    [Fact]
    public async Task An_Api_Call_Without_A_Twilio_Connection_Says_So()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var user = await RegisterAsync(client, "user@example.test");

        var response = await SendAsync(client, HttpMethod.Get, "/api/numbers", user.SessionToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_Unauthenticated_Api_Call_Is_Refused()
    {
        await using var factory = await NewFactoryAsync();
        var response = await factory.CreateClient().GetAsync("/api/numbers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_The_Password_Revokes_The_Old_Session_And_Hands_Back_A_New_One()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var user = await RegisterAsync(client, "user@example.test");

        var changed = await PostAsync<AuthPayload>(client, "/api/auth/change-password", new
        {
            currentPassword = Password,
            newPassword = "an-even-better-password",
        }, user.SessionToken);

        var oldToken = await SendAsync(client, HttpMethod.Get, "/api/auth/session", user.SessionToken);
        var newToken = await SendAsync(client, HttpMethod.Get, "/api/auth/session", changed.SessionToken);

        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newToken.StatusCode);
    }

    // ---------- administration ----------

    [Fact]
    public async Task A_Plain_User_Cannot_Reach_The_Admin_Surface()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        await RegisterAsync(client, "admin@example.test");
        var user = await RegisterAsync(client, "user@example.test");

        var response = await SendAsync(client, HttpMethod.Get, "/api/admin/users", user.SessionToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Sees_Every_Account()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");
        await RegisterAsync(client, "user@example.test");

        var users = await GetAsync<List<AdminUserPayload>>(
            client, "/api/admin/users", admin.SessionToken);

        // The webhook fixture seeds one account of its own.
        Assert.Contains(users, u => u.Email == "admin@example.test");
        Assert.Contains(users, u => u.Email == "user@example.test");
        Assert.Contains(users, u => u.Email == "owner@example.test" && u.AccountSid == "ACtest");
    }

    [Fact]
    public async Task Disabling_An_Account_Blocks_Its_Login_And_Its_Existing_Session()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");
        var user = await RegisterAsync(client, "user@example.test");

        var disable = await SendAsync(client, HttpMethod.Put,
            $"/api/admin/users/{user.UserId}/disabled", admin.SessionToken,
            new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var existingSession = await SendAsync(
            client, HttpMethod.Get, "/api/auth/session", user.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, existingSession.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "user@example.test",
            password = Password,
            platform = "test",
            supportsVoip = false,
        });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        // Re-enabling brings the account back.
        await SendAsync(client, HttpMethod.Put,
            $"/api/admin/users/{user.UserId}/disabled", admin.SessionToken,
            new { disabled = false });
        var reLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "user@example.test",
            password = Password,
            platform = "test",
            supportsVoip = false,
        });
        Assert.Equal(HttpStatusCode.OK, reLogin.StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Cannot_Disable_Or_Delete_Themselves()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");

        var disable = await SendAsync(client, HttpMethod.Put,
            $"/api/admin/users/{admin.UserId}/disabled", admin.SessionToken,
            new { disabled = true });
        var delete = await SendAsync(client, HttpMethod.Delete,
            $"/api/admin/users/{admin.UserId}", admin.SessionToken);

        Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Fact]
    public async Task The_Last_Administrator_Cannot_Be_Demoted()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");

        var demote = await SendAsync(client, HttpMethod.Put,
            $"/api/admin/users/{admin.UserId}/role", admin.SessionToken,
            new { isAdministrator = false });

        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
    }

    [Fact]
    public async Task Promoting_A_User_Grants_Admin_On_Their_Next_Login()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");
        var user = await RegisterAsync(client, "user@example.test");

        await SendAsync(client, HttpMethod.Put,
            $"/api/admin/users/{user.UserId}/role", admin.SessionToken,
            new { isAdministrator = true });

        // The old token was revoked by the stamp rotation that ships the role change.
        var stale = await SendAsync(client, HttpMethod.Get, "/api/auth/session", user.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var fresh = await PostAsync<AuthPayload>(client, "/api/auth/login", new
        {
            email = "user@example.test",
            password = Password,
            platform = "test",
            supportsVoip = false,
        });
        Assert.Contains("Administrator", fresh.Roles);

        var adminCall = await SendAsync(
            client, HttpMethod.Get, "/api/admin/users", fresh.SessionToken);
        Assert.Equal(HttpStatusCode.OK, adminCall.StatusCode);
    }

    [Fact]
    public async Task A_Reset_Password_Signs_The_User_Out_Everywhere()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");
        var user = await RegisterAsync(client, "user@example.test");

        var reset = await SendAsync(client, HttpMethod.Post,
            $"/api/admin/users/{user.UserId}/reset-password", admin.SessionToken,
            new { newPassword = "a-freshly-reset-password" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var stale = await SendAsync(client, HttpMethod.Get, "/api/auth/session", user.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var login = await PostAsync<AuthPayload>(client, "/api/auth/login", new
        {
            email = "user@example.test",
            password = "a-freshly-reset-password",
            platform = "test",
            supportsVoip = false,
        });
        Assert.Equal("user@example.test", login.Email);
    }

    [Fact]
    public async Task Deleting_A_User_Removes_The_Account()
    {
        await using var factory = await NewFactoryAsync();
        var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "admin@example.test");
        var user = await RegisterAsync(client, "user@example.test");

        var delete = await SendAsync(client, HttpMethod.Delete,
            $"/api/admin/users/{user.UserId}", admin.SessionToken);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var stale = await SendAsync(client, HttpMethod.Get, "/api/auth/session", user.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "user@example.test",
            password = Password,
            platform = "test",
            supportsVoip = false,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // ---------- helpers ----------

    private const string Password = "a-good-password";

    private static async Task<ApiFactory> NewFactoryAsync()
    {
        var factory = new ApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();
        return factory;
    }

    private static async Task<AuthPayload> RegisterAsync(HttpClient client, string email) =>
        await PostAsync<AuthPayload>(client, "/api/auth/register", new
        {
            email,
            password = Password,
            displayName = email.Split('@')[0],
            platform = "test",
            supportsVoip = false,
        });

    private static async Task<T> PostAsync<T>(
        HttpClient client, string path, object body, string? token = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, path, token, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, string token)
    {
        var response = await SendAsync(client, HttpMethod.Get, path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    private sealed record AuthPayload(
        string SessionToken, string UserId, string Email, string DisplayName,
        List<string> Roles, string Identity);

    private sealed record SessionPayload(
        string UserId, string Email, string DisplayName, List<string> Roles,
        string Identity, Dictionary<string, JsonElement>? Connection);

    private sealed record AdminUserPayload(
        string Id, string Email, string DisplayName, List<string> Roles,
        bool Disabled, string? AccountSid, int DeviceCount);
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services;

namespace TwilioCaller.Api.Endpoints;

/// <summary>
/// Registration and login, shared by the Flutter app and the web portal. Both sign
/// in with an email and password held by ASP.NET Core Identity, and receive the same
/// bearer session token the rest of the API requires.
/// </summary>
public static class AuthEndpoints
{
    public const string AdministratorRole = "Administrator";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/register", RegisterAsync).AllowAnonymous();
        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapGet("/session", GetSessionAsync).RequireAuthorization();
        auth.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<AppUser> users,
        RoleManager<IdentityRole> roles,
        ConnectionService connections,
        SessionTokenService sessions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(
                new ApiError("missing_credentials", "Email and password are both required."));
        }

        var email = request.Email.Trim();
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? email
                : request.DisplayName.Trim(),
        };

        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return Results.BadRequest(new ApiError(
                "registration_failed",
                string.Join(" ", created.Errors.Select(e => e.Description))));
        }

        // The very first account becomes the administrator, so a fresh deployment can
        // be managed without editing the database. Later accounts are plain users.
        if (!roles.Roles.Any(r => r.Name == AdministratorRole))
        {
            await roles.CreateAsync(new IdentityRole(AdministratorRole));
        }

        if ((await users.GetUsersInRoleAsync(AdministratorRole)).Count == 0)
        {
            await users.AddToRoleAsync(user, AdministratorRole);
        }

        return Results.Ok(await IssueSessionAsync(
            user, users, connections, sessions, request.Platform, request.SupportsVoip, ct));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<AppUser> users,
        ConnectionService connections,
        SessionTokenService sessions,
        CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : await users.FindByEmailAsync(request.Email.Trim());

        if (user is null)
        {
            return Results.Json(
                new ApiError("invalid_credentials", "No account matches that email and password."),
                statusCode: 401);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return Results.Json(
                new ApiError("account_locked",
                    "This account is locked. Ask an administrator to unlock it, or retry later."),
                statusCode: 403);
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return Results.Json(
                new ApiError("invalid_credentials", "No account matches that email and password."),
                statusCode: 401);
        }

        await users.ResetAccessFailedCountAsync(user);

        return Results.Ok(await IssueSessionAsync(
            user, users, connections, sessions, request.Platform, request.SupportsVoip, ct));
    }

    private static async Task<IResult> GetSessionAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        ConnectionService connections,
        CancellationToken ct)
    {
        var (userId, identity) = Caller(principal);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await connections.TouchDeviceAsync(userId, identity, ct);
        var connection = await connections.FindByUserAsync(userId, ct);

        return Results.Ok(new SessionResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            (await users.GetRolesAsync(user)).ToList(),
            identity,
            TwilioConnectionDtoOf(connection)));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SessionTokenService sessions,
        CancellationToken ct)
    {
        var (userId, identity) = Caller(principal);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var changed = await users.ChangePasswordAsync(
            user, request.CurrentPassword, request.NewPassword);
        if (!changed.Succeeded)
        {
            return Results.BadRequest(new ApiError(
                "password_change_failed",
                string.Join(" ", changed.Errors.Select(e => e.Description))));
        }

        // Changing the password rotates the security stamp, which revokes every other
        // session. Hand this one a fresh token so it stays signed in.
        var roles = await users.GetRolesAsync(user);
        var (token, expiresAt) = sessions.Issue(user.Id, identity, roles, user.SecurityStamp);

        return Results.Ok(new AuthResponse(
            token, expiresAt, user.Id, user.Email ?? string.Empty, user.DisplayName,
            roles.ToList(), identity));
    }

    internal static async Task<AuthResponse> IssueSessionAsync(
        AppUser user,
        UserManager<AppUser> users,
        ConnectionService connections,
        SessionTokenService sessions,
        string? platform,
        bool supportsVoip,
        CancellationToken ct)
    {
        var device = await connections.RegisterDeviceAsync(user.Id, platform, supportsVoip, ct);
        var roles = await users.GetRolesAsync(user);
        var (token, expiresAt) = sessions.Issue(user.Id, device.Identity, roles, user.SecurityStamp);

        return new AuthResponse(
            token, expiresAt, user.Id, user.Email ?? string.Empty, user.DisplayName,
            roles.ToList(), device.Identity);
    }

    internal static TwilioConnectionDto? TwilioConnectionDtoOf(TwilioConnection? connection) =>
        connection is null
            ? null
            : new TwilioConnectionDto(
                connection.Id,
                connection.AccountSid,
                connection.FriendlyName,
                VoiceReady: !string.IsNullOrEmpty(connection.TwimlAppSid),
                connection.TwimlAppSid,
                connection.FallbackForwardNumber,
                connection.CreatedAt);

    internal static (string UserId, string Identity) Caller(ClaimsPrincipal principal) => (
        principal.FindFirst(SessionTokenService.UserIdClaim)?.Value ?? string.Empty,
        principal.FindFirst(SessionTokenService.IdentityClaim)?.Value ?? string.Empty);
}

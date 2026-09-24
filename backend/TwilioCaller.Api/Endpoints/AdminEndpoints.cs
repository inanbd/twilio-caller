using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Endpoints;

/// <summary>
/// Account administration for the portal. Only members of the Administrator role
/// get here; they can see and manage every account on this backend.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin")
            .RequireAuthorization(policy => policy.RequireRole(AuthEndpoints.AdministratorRole))
            .WithTags("Admin");

        admin.MapGet("/users", ListUsersAsync);
        admin.MapGet("/users/{id}", GetUserAsync);
        admin.MapPost("/users", CreateUserAsync);
        admin.MapPut("/users/{id}/role", SetRoleAsync);
        admin.MapPut("/users/{id}/disabled", SetDisabledAsync);
        admin.MapPost("/users/{id}/reset-password", ResetPasswordAsync);
        admin.MapDelete("/users/{id}", DeleteUserAsync);
    }

    private static async Task<IResult> ListUsersAsync(
        AppDbContext db, UserManager<AppUser> users, CancellationToken ct)
    {
        var admins = (await users.GetUsersInRoleAsync(AuthEndpoints.AdministratorRole))
            .Select(u => u.Id)
            .ToHashSet();

        var rows = await db.Users
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.LockoutEnd,
                u.CreatedAt,
                Connection = db.Connections
                    .Where(c => c.UserId == u.Id)
                    .Select(c => new { c.AccountSid, c.FriendlyName })
                    .FirstOrDefault(),
                DeviceCount = db.Devices.Count(d => d.UserId == u.Id),
                LastSeenAt = db.Devices
                    .Where(d => d.UserId == u.Id)
                    .Max(d => (DateTimeOffset?)d.LastSeenAt),
            })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return Results.Ok(rows.Select(u => new AdminUserSummary(
            u.Id,
            u.Email ?? string.Empty,
            u.DisplayName,
            admins.Contains(u.Id) ? new[] { AuthEndpoints.AdministratorRole } : Array.Empty<string>(),
            Disabled: u.LockoutEnd > now,
            u.CreatedAt,
            u.Connection?.AccountSid,
            u.Connection?.FriendlyName,
            u.DeviceCount,
            u.LastSeenAt)));
    }

    private static async Task<IResult> GetUserAsync(
        string id, AppDbContext db, UserManager<AppUser> users, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return Results.NotFound(new ApiError("no_such_user"));
        }

        var connection = await db.Connections.FirstOrDefaultAsync(c => c.UserId == id, ct);
        var devices = await db.Devices
            .Where(d => d.UserId == id)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new AdminDeviceDto(d.Id, d.Identity, d.Platform, d.SupportsVoip, d.LastSeenAt))
            .ToListAsync(ct);

        return Results.Ok(new AdminUserDetail(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            (await users.GetRolesAsync(user)).ToList(),
            Disabled: user.LockoutEnd > DateTimeOffset.UtcNow,
            user.CreatedAt,
            AuthEndpoints.TwilioConnectionDtoOf(connection),
            devices));
    }

    private static async Task<IResult> CreateUserAsync(
        AdminCreateUserRequest request, UserManager<AppUser> users,
        RoleManager<IdentityRole> roles)
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
                "user_creation_failed",
                string.Join(" ", created.Errors.Select(e => e.Description))));
        }

        if (request.IsAdministrator)
        {
            if (!roles.Roles.Any(r => r.Name == AuthEndpoints.AdministratorRole))
            {
                await roles.CreateAsync(new IdentityRole(AuthEndpoints.AdministratorRole));
            }

            await users.AddToRoleAsync(user, AuthEndpoints.AdministratorRole);
        }

        return Results.Ok(new { user.Id });
    }

    private static async Task<IResult> SetRoleAsync(
        string id, AdminSetRoleRequest request, UserManager<AppUser> users,
        RoleManager<IdentityRole> roles)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return Results.NotFound(new ApiError("no_such_user"));
        }

        var isAdmin = await users.IsInRoleAsync(user, AuthEndpoints.AdministratorRole);
        if (request.IsAdministrator == isAdmin)
        {
            return Results.Ok();
        }

        if (request.IsAdministrator)
        {
            if (!roles.Roles.Any(r => r.Name == AuthEndpoints.AdministratorRole))
            {
                await roles.CreateAsync(new IdentityRole(AuthEndpoints.AdministratorRole));
            }

            await users.AddToRoleAsync(user, AuthEndpoints.AdministratorRole);
        }
        else
        {
            var admins = await users.GetUsersInRoleAsync(AuthEndpoints.AdministratorRole);
            if (admins.Count <= 1)
            {
                return Results.BadRequest(new ApiError(
                    "last_administrator",
                    "This is the only administrator. Promote another account first."));
            }

            await users.RemoveFromRoleAsync(user, AuthEndpoints.AdministratorRole);
        }

        // Roles ride inside the session token, so the change only takes effect once
        // the user's existing tokens die. Rotating the stamp kills them now.
        await users.UpdateSecurityStampAsync(user);
        return Results.Ok();
    }

    private static async Task<IResult> SetDisabledAsync(
        string id, AdminSetDisabledRequest request, ClaimsPrincipal principal,
        UserManager<AppUser> users)
    {
        var (callerId, _) = AuthEndpoints.Caller(principal);
        if (request.Disabled && id == callerId)
        {
            return Results.BadRequest(new ApiError(
                "cannot_disable_self", "You cannot disable your own account."));
        }

        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return Results.NotFound(new ApiError("no_such_user"));
        }

        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(
            user, request.Disabled ? DateTimeOffset.MaxValue : null);
        return Results.Ok();
    }

    private static async Task<IResult> ResetPasswordAsync(
        string id, AdminResetPasswordRequest request, UserManager<AppUser> users)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return Results.NotFound(new ApiError("no_such_user"));
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!reset.Succeeded)
        {
            return Results.BadRequest(new ApiError(
                "password_reset_failed",
                string.Join(" ", reset.Errors.Select(e => e.Description))));
        }

        // A reset password means the old holder must not stay signed in anywhere.
        await users.UpdateSecurityStampAsync(user);
        return Results.Ok();
    }

    private static async Task<IResult> DeleteUserAsync(
        string id, ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users,
        CancellationToken ct)
    {
        var (callerId, _) = AuthEndpoints.Caller(principal);
        if (id == callerId)
        {
            return Results.BadRequest(new ApiError(
                "cannot_delete_self", "You cannot delete your own account."));
        }

        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return Results.NotFound(new ApiError("no_such_user"));
        }

        // The connection and devices cascade from the user row, but inbound events
        // are only keyed by connection id, so they are cleaned up explicitly.
        var connectionIds = await db.Connections
            .Where(c => c.UserId == id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (connectionIds.Count > 0)
        {
            await db.InboundEvents
                .Where(e => connectionIds.Contains(e.ConnectionId))
                .ExecuteDeleteAsync(ct);
        }

        var deleted = await users.DeleteAsync(user);
        return deleted.Succeeded
            ? Results.Ok()
            : Results.BadRequest(new ApiError(
                "user_deletion_failed",
                string.Join(" ", deleted.Errors.Select(e => e.Description))));
    }
}

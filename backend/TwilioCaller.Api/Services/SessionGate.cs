using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Data;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Session tokens live for weeks, so possession alone must not be enough: this
/// middleware re-checks the account behind every authenticated request. A deleted or
/// disabled account, or a rotated security stamp (password reset, role change),
/// turns an otherwise valid token into a 401 immediately.
/// </summary>
public static class SessionGate
{
    public static IApplicationBuilder UseSessionGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var userId = context.User.FindFirst(SessionTokenService.UserIdClaim)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                await next(context);
                return;
            }

            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            var account = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.LockoutEnd, u.SecurityStamp })
                .FirstOrDefaultAsync(context.RequestAborted);

            var stamp = context.User.FindFirst(SessionTokenService.StampClaim)?.Value;
            if (account is null ||
                account.LockoutEnd > DateTimeOffset.UtcNow ||
                (stamp is not null && stamp != account.SecurityStamp))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new Contracts.ApiError("session_revoked",
                        "This session is no longer valid. Sign in again."));
                return;
            }

            await next(context);
        });
}

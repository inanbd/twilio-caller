using Microsoft.AspNetCore.Identity;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Endpoints;

namespace TwilioCaller.Api.Services;

/// <summary>
/// Startup seeding: the Administrator role always exists, and an operator can
/// pre-provision an admin account from the environment (Admin__Email /
/// Admin__Password) instead of relying on being the first to register.
/// </summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("IdentitySeeder");

        if (!await roles.RoleExistsAsync(AuthEndpoints.AdministratorRole))
        {
            await roles.CreateAsync(new IdentityRole(AuthEndpoints.AdministratorRole));
        }

        var email = config["Admin:Email"];
        var password = config["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new AppUser { UserName = email, Email = email, DisplayName = "Administrator" };
            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded)
            {
                // A weak seeded password must not stop the API from serving webhooks.
                logger.LogError("Could not create the seeded admin {Email}: {Errors}",
                    email, string.Join(" ", created.Errors.Select(e => e.Description)));
                return;
            }
        }

        if (!await users.IsInRoleAsync(admin, AuthEndpoints.AdministratorRole))
        {
            await users.AddToRoleAsync(admin, AuthEndpoints.AdministratorRole);
        }
    }
}

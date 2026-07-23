using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Infrastructure.Seed;

public static class DbInitializer
{
    private static readonly string[] RoleNames = Enum.GetNames<UserRole>();

    public static async Task SeedRolesAsync(IServiceProvider serviceProvider)
    {
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var roleName in RoleNames)
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
            EnsureSucceeded(result, $"create the '{roleName}' role");
        }
    }

    /// <summary>
    /// Provisions the initial root account using the existing Admin role. In
    /// this application Admin is the super-administrator role; creating a
    /// second overlapping role would make the old authorization attributes
    /// inconsistent.
    /// </summary>
    public static async Task SeedAdminAsync(IServiceProvider serviceProvider)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        var email = configuration["AdminSeed:Email"]
            ?? throw new InvalidOperationException("AdminSeed:Email is required when SeedAdmin=true.");
        var password = configuration["AdminSeed:Password"]
            ?? throw new InvalidOperationException("AdminSeed:Password is required when SeedAdmin=true.");
        var name = configuration["AdminSeed:Name"] ?? "Moore Hotels Super Administrator";

        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email.Trim().ToLowerInvariant(),
                Email = email.Trim().ToLowerInvariant(),
                Name = name.Trim(),
                Role = UserRole.Admin,
                Status = ProfileStatus.Active,
                EmailConfirmed = true,
                LockoutEnabled = true,
                CreatedAt = DateTime.UtcNow
            };

            EnsureSucceeded(
                await userManager.CreateAsync(admin, password),
                "create the initial super administrator");
            logger.LogInformation("Initial super administrator account created.");
        }

        if (admin.Role != UserRole.Admin || admin.Status != ProfileStatus.Active || !admin.EmailConfirmed)
        {
            admin.Role = UserRole.Admin;
            admin.Status = ProfileStatus.Active;
            admin.EmailConfirmed = true;
            EnsureSucceeded(await userManager.UpdateAsync(admin), "repair the initial super administrator");
        }

        if (!await userManager.IsInRoleAsync(admin, nameof(UserRole.Admin)))
        {
            EnsureSucceeded(
                await userManager.AddToRoleAsync(admin, nameof(UserRole.Admin)),
                "assign the Admin role to the initial super administrator");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));
        throw new InvalidOperationException($"Failed to {action}. {errors}");
    }
}

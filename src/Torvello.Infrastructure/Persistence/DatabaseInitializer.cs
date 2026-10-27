using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Torvello.Domain;

namespace Torvello.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Database:Initialize = "Migrate" (default), "EnsureCreated" (tests) or "None".
    /// Then makes sure the roles exist and creates the first owner from Bootstrap settings.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();

        switch (configuration["Database:Initialize"] ?? "Migrate")
        {
            case "Migrate":
                await db.Database.MigrateAsync();
                break;
            case "EnsureCreated":
                await db.Database.EnsureCreatedAsync();
                break;
            case "None":
                return;
        }

        var roles = provider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole<Guid>(role));
        }

        var bootstrap = provider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        var users = provider.GetRequiredService<UserManager<AppUser>>();
        if (await users.Users.AnyAsync() || string.IsNullOrWhiteSpace(bootstrap.OwnerEmail) || string.IsNullOrEmpty(bootstrap.OwnerPassword))
        {
            return;
        }

        var owner = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = bootstrap.OwnerEmail.Trim(),
            Email = bootstrap.OwnerEmail.Trim(),
            DisplayName = bootstrap.OwnerName,
            EmailConfirmed = true,
            MustChangePassword = true,
        };
        // The bootstrap password only has to be long enough; the owner replaces it at first sign-in.
        var created = await users.CreateAsync(owner);
        if (created.Succeeded)
        {
            owner.PasswordHash = users.PasswordHasher.HashPassword(owner, bootstrap.OwnerPassword);
            created = await users.UpdateAsync(owner);
        }
        if (!created.Succeeded)
        {
            throw new InvalidOperationException("Could not create the bootstrap owner: " + string.Join(" ", created.Errors.Select(e => e.Description)));
        }
        await users.AddToRoleAsync(owner, Roles.Owner);
        provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer))
            .LogWarning("Created bootstrap owner {Email}. Sign in and change the password now.", owner.Email);
    }
}

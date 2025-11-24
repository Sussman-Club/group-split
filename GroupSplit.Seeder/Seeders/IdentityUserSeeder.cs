using GroupSplit.Identity;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Seeders;

public sealed class IdentityUserSeeder(
    UserManager<User> userManager,
    // RoleManager<IdentityRole> roleManager,
    ILogger<IdentityUserSeeder> logger,
    IOptions<SeederOptions> options)
    : JsonSeeder<User, IdentityUserSeedDto>(options.Value.Paths.IdentityUsers, logger)
{
    protected override async Task AddEntityAsync(User entity, IdentityUserSeedDto dto, CancellationToken ct = default)
    {
        var result = await userManager.CreateAsync(entity, dto.Password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create user {User}: {Errors}",
                dto.UserName, string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        // if (dto.Roles != null)
        // {
        //     foreach (var role in dto.Roles)
        //     {
        //         if (!await roleManager.RoleExistsAsync(role))
        //             await roleManager.CreateAsync(new IdentityRole(role));
        //
        //         await userManager.AddToRoleAsync(user, role);
        //     }
        // }

        logger.LogInformation("Seeded user {User}", dto.UserName);
    }

    protected override async Task<User?> ConvertEntityAsync(IdentityUserSeedDto dto, CancellationToken ct = default)
    {
        var existing = await userManager.FindByNameAsync(dto.UserName);
        if (existing != null)
        {
            logger.LogInformation("User '{User}' already exists. Skipping.", dto.UserName);
            return null;
        }

        var user = new User
        {
            Id = dto.Id,
            UserName = dto.UserName,
            Email = dto.Email,
            EmailConfirmed = dto.EmailConfirmed
        };

        return user;
    }
}
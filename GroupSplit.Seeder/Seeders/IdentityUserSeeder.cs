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
        var existing = await userManager.FindByIdAsync(dto.Id);
        if (existing is not null)
        {
            logger.LogInformation("User with id {Id} already exists. Skipping.", dto.Id);
            return;
        }

        var result = await userManager.CreateAsync(entity, dto.Password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create user {User}: {Errors}",
                dto.UserName, string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        // if (dto.Roles is not null)
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

    protected override Task<User?> ConvertEntityAsync(IdentityUserSeedDto dto, CancellationToken ct = default)
    {
        var user = new User
        {
            Id = dto.Id,
            UserName = dto.UserName,
            Email = dto.Email,
            EmailConfirmed = dto.EmailConfirmed
        };

        return Task.FromResult(user)!;
    }
}
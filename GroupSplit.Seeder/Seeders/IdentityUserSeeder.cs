using GroupSplit.Identity;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.AspNetCore.Identity;

namespace GroupSplit.Seeder.Seeders;

public class IdentityUserSeeder(
    ISeedDataSource<IdentityUserSeedDto> source,
    UserManager<User> userManager,
    // RoleManager<IdentityRole> roleManager,
    ILogger<IdentityUserSeeder> logger)
    : ISeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await foreach (var dto in source.ReadAsync(ct))
        {
            if (await userManager.FindByIdAsync(dto.Id) is not null)
            {
                logger.LogInformation("User with id {Id} already exists. Skipping.", dto.Id);
                continue;
            }

            var user = new User
            {
                Id = dto.Id,
                UserName = dto.UserName,
                Email = dto.Email,
                EmailConfirmed = dto.EmailConfirmed
            };

            var result = await userManager.CreateAsync(user, dto.Password);

            if (!result.Succeeded)
            {
                logger.LogError("Failed to create user {User}: {Errors}", dto.UserName,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }

            // Uncomment to seed roles
            // if (dto.Roles is {Count: > 0})
            // {
            //     foreach (var role in dto.Roles)
            //     {
            //         if (!await roleManager.RoleExistsAsync(role))
            //             await roleManager.CreateAsync(new IdentityRole(role));
            //
            //         await userManager.AddToRoleAsync(user, role);
            //     }
            // }
        }
    }
}
using System.Text.Json;
using GroupSplit.Identity;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Seeder.Seeders;

public sealed class IdentityUserSeeder(
    UserManager<User> userManager,
    // RoleManager<IdentityRole> roleManager,
    string jsonPath,
    ILogger<IdentityUserSeeder> logger)
    : IDatabaseSeeder
{
    public int Order => 0;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("File not found: {JsonPath}", jsonPath);
            return;
        }

        logger.LogInformation("Loading identity users from {JsonPath}", jsonPath);

        var json = await File.ReadAllTextAsync(jsonPath, ct);
        var dtos = JsonSerializer.Deserialize<List<IdentityUserSeedDto>>(json);

        if (dtos is not { Count: > 0 })
        {
            logger.LogWarning("No user entries found.");
            return;
        }

        foreach (var dto in dtos)
        {
            var existing = await userManager.FindByNameAsync(dto.UserName);
            if (existing != null)
            {
                logger.LogInformation("User '{User}' already exists. Skipping.", dto.UserName);
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
                logger.LogError("Failed to create user {User}: {Errors}",
                    dto.UserName, string.Join("; ", result.Errors.Select(e => e.Description)));
                continue;
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
    }
}

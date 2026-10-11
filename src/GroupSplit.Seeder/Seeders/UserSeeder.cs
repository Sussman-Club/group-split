using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(GroupSeeder))]
public class UserSeeder(AppDbContext db, ILogger<UserSeeder> logger, ISeedDataSource<UserSeedDto> source)
    : AppDbContextSeeder<User, UserSeedDto>(db, source, logger)
{
    protected override async Task<User?> MapAsync(UserSeedDto dto, CancellationToken ct = default)
    {
        var user = new User
        {
            Id = dto.Id,
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Email = dto.Email,
            Identity = new UserIdentity { IdentityId = dto.ExternalUserId },
        };

        foreach (var groupId in dto.GroupIds)
        {
            if (await DbContext.Set<Group>().FindAsync([groupId], ct) is { } group)
                user.Groups.Add(group);
        }

        return user;
    }

    /// <summary>
    /// The user, and the personal group every account has, the way provisioning makes both.
    /// Only for a user being added, so a second run makes no second group.
    /// </summary>
    protected override Task AddEntityAsync(User entity, UserSeedDto dto, CancellationToken ct = default)
    {
        DbContext.Add(entity);
        DbContext.Add(entity.NewPersonalGroup());
        return Task.CompletedTask;
    }
}
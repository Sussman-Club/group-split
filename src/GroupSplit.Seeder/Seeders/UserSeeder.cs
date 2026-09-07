using GroupSplit.Data;
using GroupSplit.Data.Entities;
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

        // No personal group. An expense of one's own is one with no group at all, so the
        // hidden group that used to stand in for it -- and appeared in the switcher, on the
        // home page and in every count of somebody's groups -- is not seeded either.
        foreach (var groupId in dto.GroupIds)
        {
            if (await DbContext.Set<Group>().FindAsync([groupId], ct) is { } group)
                user.Groups.Add(group);
        }

        return user;
    }
}
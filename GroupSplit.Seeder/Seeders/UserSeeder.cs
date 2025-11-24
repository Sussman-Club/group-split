using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Seeders;

public class UserSeeder(AppDbContext db, ILogger<UserSeeder> logger, IOptions<SeederOptions> options)
    : DbContextJsonSeeder<User, UserSeedDto, AppDbContext>(db, options.Value.Paths.Users, logger, 1)
{
    protected override async Task<User?> ConvertEntityAsync(UserSeedDto dto, CancellationToken ct = default)
    {
        var user = new User
        {
            Identity = new UserIdentity
            {
                IdentityId = dto.ExternalUserId
            }
        };

        if (dto.PersonalGroupId is not null)
        {
            user.PersonalGroup = await DbContext.Set<Group>().FindAsync([dto.PersonalGroupId.Value], ct);
            if (user.PersonalGroup != null) user.Groups.Add(user.PersonalGroup);
        }

        foreach (var gid in dto.GroupIds)
        {
            var group = await DbContext.Set<Group>().FindAsync([gid], ct);
            if (group != null)
                user.Groups.Add(group);
        }

        return user;
    }
}
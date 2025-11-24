using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Seeders;

public class UserSeeder(AppDbContext db, ILogger<UserSeeder> logger, IOptions<SeederOptions> options)
    : AppDbContextJsonSeeder<User, UserSeedDto>(db, options.Value.Paths.Users, logger, SeederOrder.Users)
{
    protected override async Task<User?> ConvertEntityAsync(UserSeedDto dto, CancellationToken ct = default)
    {
        var user = new User
        {
            Id = dto.Id,
            Identity = new UserIdentity
            {
                IdentityId = dto.ExternalUserId
            }
        };

        if (dto.PersonalGroupId is not null)
        {
            var personalGroup = await DbContext.Set<Group>().FindAsync([dto.PersonalGroupId.Value], ct);
            if (personalGroup is not null)
            {
                user.PersonalGroup = personalGroup;
                user.Groups.Add(personalGroup);
            }
        }

        foreach (var gid in dto.GroupIds)
        {
            var group = await DbContext.Set<Group>().FindAsync([gid], ct);
            if (group is not null)
                user.Groups.Add(group);
        }

        return user;
    }
}
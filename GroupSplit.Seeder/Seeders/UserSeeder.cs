using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(GroupSeeder))]
public class UserSeeder(AppDbContext db, ILogger<UserSeeder> logger, IOptions<SeederOptions> options)
    : AppDbContextJsonSeeder<User, UserSeedDto>(db, options.Value.Paths.Users, logger)
{
    protected override async Task<User?> ConvertEntityAsync(UserSeedDto dto, CancellationToken ct = default)
    {
        var user = new User
        {
            Id = dto.Id,
            Identity = new UserIdentity { IdentityId = dto.ExternalUserId }
        };

        if (dto.PersonalGroupId is { } personalGroupId &&
            await DbContext.Set<Group>().FindAsync([personalGroupId], ct) is { } personalGroup)
        {
            user.PersonalGroup = personalGroup;
            user.Groups.Add(personalGroup);
        }

        foreach (var groupId in dto.GroupIds)
        {
            if (await DbContext.Set<Group>().FindAsync([groupId], ct) is { } group)
                user.Groups.Add(group);
        }

        return user;
    }
}
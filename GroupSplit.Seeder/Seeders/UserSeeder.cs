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

        var personalGroup = new Group
        {
            Name = "Personal",
            Rules =
            {
                new Rule
                {
                    Category = "Personal",
                    Versions =
                    {
                        new PersonalRuleVersion
                        {
                            StartDateTime = DateTimeOffset.UtcNow
                        }
                    }
                }
            }
        };

        user.PersonalGroup = personalGroup;
        user.Groups.Add(personalGroup);

        foreach (var groupId in dto.GroupIds)
        {
            if (await DbContext.Set<Group>().FindAsync([groupId], ct) is { } group)
                user.Groups.Add(group);
        }

        return user;
    }
}
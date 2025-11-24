using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Seeder.Seeders;

public class UserSeeder(AppDbContext db, string path, ILogger<UserSeeder> logger)
    : JsonSeeder<User, UserSeedDto, AppDbContext>(db, path, logger)
{
    private readonly AppDbContext _db = db;
    public override int Order => 1;

    protected override async Task<User?> ConvertEntityAsync(UserSeedDto? dto, CancellationToken ct = default)
    {
        if (dto is null) return null;

        var user = new User
        {
            Identity = new UserIdentity
            {
                IdentityId = dto.ExternalUserId
            }
        };

        if (dto.PersonalGroupId is not null)
            user.PersonalGroup = await _db.Set<Group>().FindAsync([dto.PersonalGroupId.Value], ct);

        foreach (var gid in dto.GroupIds)
        {
            var group = await _db.Set<Group>().FindAsync([gid], ct);
            if (group != null)
                user.Groups.Add(group);
        }

        return user;
    }
}
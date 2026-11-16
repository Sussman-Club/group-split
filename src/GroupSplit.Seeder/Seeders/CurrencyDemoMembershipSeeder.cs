using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>Adds the new demo group's members on fresh and previously seeded databases.</summary>
[DependsOn(typeof(GroupSeeder))]
[DependsOn(typeof(UserSeeder))]
public sealed class CurrencyDemoMembershipSeeder(AppDbContext db, TimeProvider clock) : ISeeder
{
    private static readonly Guid GroupId = Guid.Parse("45500001-0000-4000-8000-000000000001");
    private static readonly Guid[] Members =
    [
        Guid.Parse("B2C1F8E1-8B2A-4E77-9A29-8F0E570A4F64"),
        Guid.Parse("94F67FC0-F549-46ED-AD81-D6DF79225396")
    ];

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!await db.Set<Group>().AnyAsync(group => group.Id == GroupId, ct))
        {
            return;
        }

        var existing = await db.Set<GroupMembership>().IgnoreQueryFilters()
            .Where(membership => membership.GroupId == GroupId && Members.Contains(membership.UserId))
            .Select(membership => membership.UserId).ToListAsync(ct);
        var users = await db.Set<User>().Where(user => Members.Contains(user.Id))
            .Select(user => user.Id).ToListAsync(ct);
        foreach (var id in users.Except(existing))
        {
            db.Add(new GroupMembership { GroupId = GroupId, UserId = id, JoinedAt = clock.GetUtcNow() });
        }

        await db.SaveChangesAsync(ct);
    }
}

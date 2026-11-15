using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Test.Seeding;

public class CurrencyDemoMembershipSeederTest
{
    [Fact]
    public async Task Adds_missing_demo_members_once_without_reinstating_a_departed_member()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"currency-members-{Guid.NewGuid()}").Options);
        var groupId = Guid.Parse("45500001-0000-4000-8000-000000000001");
        var ana = Guid.Parse("B2C1F8E1-8B2A-4E77-9A29-8F0E570A4F64");
        var daniel = Guid.Parse("94F67FC0-F549-46ED-AD81-D6DF79225396");
        db.AddRange(new Group { Id = groupId, Name = "Lisbon", Currency = "EUR" },
            new User { Id = ana, FirstName = "Anabel" }, new User { Id = daniel, FirstName = "Daniel" },
            new GroupMembership { GroupId = groupId, UserId = daniel, LeftAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        var seeder = new CurrencyDemoMembershipSeeder(db, TimeProvider.System);
        await seeder.SeedAsync(ct);
        await seeder.SeedAsync(ct);
        var memberships = await db.Set<GroupMembership>().IgnoreQueryFilters().Where(row => row.GroupId == groupId).ToListAsync(ct);
        Assert.Equal(2, memberships.Count);
        Assert.Null(memberships.Single(row => row.UserId == ana).LeftAt);
        Assert.NotNull(memberships.Single(row => row.UserId == daniel).LeftAt);
    }
}

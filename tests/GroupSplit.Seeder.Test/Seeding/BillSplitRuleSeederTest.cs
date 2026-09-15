using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

public class BillSplitRuleSeederTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeds_one_bill_rule_for_every_group_and_is_idempotent()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bill-rules-{Guid.NewGuid()}")
            .Options);

        var provisioned = new Group { Name = "Already provisioned" };
        var missing = new Group { Name = "Missing" };
        var existingRule = new SplitRule
        {
            Group = provisioned,
            BuiltIn = true,
            Name = "Divide by the bill",
            Versions = { new ItemizedSplitRuleVersion() }
        };

        db.AddRange(provisioned, missing, existingRule);
        await db.SaveChangesAsync(Ct);

        var seeder = new BillSplitRuleSeeder(
            db,
            NullLogger<BillSplitRuleSeeder>.Instance,
            new BillSplitRule(db));

        await seeder.SeedAsync(Ct);
        var firstRun = await CurrentRules(db, Ct);

        Assert.Equal(2, firstRun.Count);
        Assert.All(firstRun, rule =>
        {
            Assert.True(rule.BuiltIn);
            Assert.Single(rule.Versions);
            Assert.IsType<ItemizedSplitRuleVersion>(rule.Versions.Single());
        });

        await seeder.SeedAsync(Ct);
        var secondRun = await CurrentRules(db, Ct);

        Assert.Equal(firstRun.Select(rule => rule.Id).OrderBy(id => id),
            secondRun.Select(rule => rule.Id).OrderBy(id => id));
    }

    private static Task<List<SplitRule>> CurrentRules(AppDbContext db, CancellationToken ct) =>
        db.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .Where(rule => rule.Versions.Any(version => version.SupersededAt == null))
            .ToListAsync(ct);
}

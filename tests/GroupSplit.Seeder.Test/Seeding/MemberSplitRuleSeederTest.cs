using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

public class MemberSplitRuleSeederTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeds_one_personal_rule_per_member_and_is_idempotent()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"member-rules-{Guid.NewGuid()}")
            .Options);

        var group = new Group { Name = "Flat" };
        var ana = new User { FirstName = "Ana", LastName = "B", Email = "ana@example.com" };
        var dan = new User { FirstName = "Dan", LastName = "C", Email = "dan@example.com" };
        group.Users.Add(ana);
        group.Users.Add(dan);

        var existingRule = new SplitRule
        {
            Group = group,
            BuiltIn = true,
            Name = "All for Ana",
            Versions = { new SoleSplitRuleVersion { UserId = ana.Id } }
        };

        db.AddRange(group, ana, dan, existingRule);
        await db.SaveChangesAsync(Ct);

        var seeder = new MemberSplitRuleSeeder(
            db,
            NullLogger<MemberSplitRuleSeeder>.Instance,
            new MemberSplitRules(db));

        await seeder.SeedAsync(Ct);
        var firstRun = await CurrentRules(db, Ct);

        Assert.Equal(2, firstRun.Count);
        Assert.Equal(2, firstRun
            .SelectMany(rule => rule.Versions)
            .OfType<SoleSplitRuleVersion>()
            .Select(version => version.UserId)
            .Distinct()
            .Count());
        Assert.All(firstRun, rule => Assert.True(rule.BuiltIn));

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

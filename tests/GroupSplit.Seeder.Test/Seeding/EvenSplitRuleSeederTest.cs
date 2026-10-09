using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.DataSources;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

/// <summary>
/// A seeded group has the built-in "Even" rule and defaults to it, as a group made through
/// the API does.
/// </summary>
public class EvenSplitRuleSeederTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Against the real <c>groups.json</c>, so a group added to the seed file is covered
    /// without anybody remembering to add it here.
    /// </summary>
    [Fact]
    public async Task Every_seeded_group_defaults_to_one_even_rule_however_often_it_runs()
    {
        await using var db = NewDatabase();

        await new GroupSeeder(db, NullLogger<GroupSeeder>.Instance, new JsonArrayFileDataSource<GroupSeedDto>(
                Path.Combine(SeedData(), "groups.json"),
                NullLogger<JsonArrayFileDataSource<GroupSeedDto>>.Instance))
            .SeedAsync(Ct);

        var groups = await db.Set<Group>().AsNoTracking().ToListAsync(Ct);
        Assert.NotEmpty(groups);

        var seeder = Seeder(db);

        await seeder.SeedAsync(Ct);
        var firstRun = await EvenRules(db);

        await seeder.SeedAsync(Ct);
        var secondRun = await EvenRules(db);

        // One each, and the same one the second time round.
        Assert.Equal(groups.Select(group => group.Id).Order(), firstRun.Select(rule => rule.Group.Id).Order());
        Assert.Equal(firstRun.Select(rule => rule.Id).Order(), secondRun.Select(rule => rule.Id).Order());

        var defaults = await db.Set<Group>().AsNoTracking()
            .ToDictionaryAsync(group => group.Id, group => group.DefaultSplitRuleId, Ct);

        Assert.All(secondRun, rule =>
        {
            Assert.True(rule.BuiltIn);
            Assert.Equal("Even", rule.Name);
            Assert.Equal(rule.Id, defaults[rule.Group.Id]);
        });
    }

    /// <summary>
    /// A group that already divides by something else is left dividing by it. The rule is
    /// still given -- it is what "Even" in the expense editor is -- but the choice of default
    /// is somebody's, and re-seeding a database somebody has used must not undo it.
    /// </summary>
    [Fact]
    public async Task A_group_that_already_names_a_default_keeps_it()
    {
        await using var db = NewDatabase();

        var group = new Group { Name = "Flat" };
        var chosen = new SplitRule
        {
            Group = group,
            Name = "Rent",
            Versions = { new SharesSplitRuleVersion() }
        };
        group.DefaultSplitRule = chosen;

        db.AddRange(group, chosen);
        await db.SaveChangesAsync(Ct);

        await Seeder(db).SeedAsync(Ct);

        var even = Assert.Single(await EvenRules(db));
        Assert.Equal(group.Id, even.Group.Id);

        var stored = await db.Set<Group>().AsNoTracking().SingleAsync(Ct);
        Assert.Equal(chosen.Id, stored.DefaultSplitRuleId);
    }

    private static AppDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"even-rules-{Guid.NewGuid()}")
            .Options);

    private static EvenSplitRuleSeeder Seeder(AppDbContext db) =>
        new(db, NullLogger<EvenSplitRuleSeeder>.Instance, new EvenSplitRule(db));

    private static Task<List<SplitRule>> EvenRules(AppDbContext db) =>
        db.Set<SplitRule>()
            .AsNoTracking()
            .Include(rule => rule.Group)
            .Where(rule => rule.Versions.Any(version =>
                version.SupersededAt == null && version is EvenSplitRuleVersion))
            .ToListAsync(Ct);

    private static string SeedData([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../..", "src/GroupSplit.Seeder/SeedData"));
}

using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Seeds a group's categories, each with the split rule it defaults to.
/// </summary>
/// <remarks>
/// Two rows out of one seed entry: the rule is named after the category that points at it,
/// which is what a group starts with before anybody notices that Groceries and Utilities
/// divide the same way and points them at one rule.
/// </remarks>
[DependsOn(typeof(GroupSeeder))]
[DependsOn(typeof(UserSeeder))]
public class CategorySeeder(
    AppDbContext db,
    ILogger<CategorySeeder> logger,
    ISplitRuleFactory splitRules,
    ISeedDataSource<CategorySeedDto> source)
    : AppDbContextSeeder<Category, CategorySeedDto>(db, source, logger)
{
    /// <summary>
    /// When every seeded rule is taken to have started: before the oldest seeded expense,
    /// which reaches back to early 2023.
    /// </summary>
    private static readonly DateTimeOffset Inception = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected override async Task<Category?> MapAsync(CategorySeedDto dto, CancellationToken ct = default)
    {
        var group = await DbContext.Set<Group>().FindAsync([dto.GroupId], ct);

        if (group is null)
            return null;

        var first = splitRules.FromDto(dto.SplitRule);

        // A version dates itself to the moment it is made, which for a seeded one is the
        // moment the seeder ran -- after every expense it is supposed to have divided. The
        // app reads that comparison and says so: the strip over the shares called every
        // seeded expense in the demo data a record that "looks wrong", because on the dates
        // stored it was. Backdated to before the oldest seeded expense, which is what a rule
        // the group has always had would look like.
        first.StartedAt = Inception;

        // A rule and its first version, which is what a rule that has never been edited is.
        var rule = new SplitRule
        {
            Group = group,
            Name = dto.Category,
            Versions = { first }
        };

        return new Category
        {
            Id = dto.Id,
            Name = dto.Category,
            Group = group,
            DefaultSplitRule = rule
        };
    }
}

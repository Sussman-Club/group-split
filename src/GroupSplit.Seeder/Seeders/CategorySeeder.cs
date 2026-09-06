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
    protected override async Task<Category?> MapAsync(CategorySeedDto dto, CancellationToken ct = default)
    {
        var group = await DbContext.Set<Group>().FindAsync([dto.GroupId], ct);

        if (group is null)
            return null;

        var rule = splitRules.FromDto(dto.Category, dto.SplitRule);
        rule.Group = group;

        return new Category
        {
            Id = dto.Id,
            Name = dto.Category,
            Group = group,
            DefaultSplitRule = rule
        };
    }
}

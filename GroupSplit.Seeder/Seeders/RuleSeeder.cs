using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(GroupSeeder))]
public class RuleSeeder(AppDbContext db, ILogger<RuleSeeder> logger, ISeedDataSource<RuleSeedDto> source)
    : AppDbContextSeeder<Rule, RuleSeedDto>(db, source, logger)
{
    protected override async Task<Rule?> MapAsync(RuleSeedDto dto, CancellationToken ct = default)
    {
        var group = await DbContext.Set<Group>().FindAsync([dto.GroupId], ct);

        if (group is null)
            return null;

        return new Rule
        {
            Id = dto.Id,
            Category = dto.Category,
            Group = group,
        };
    }
}
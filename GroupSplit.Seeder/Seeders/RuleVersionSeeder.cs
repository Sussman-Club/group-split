using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(RuleSeeder))]
public class RuleVersionSeeder(
    AppDbContext db,
    ILogger<RuleVersionSeeder> logger,
    ISeedDataSource<RuleVersionSeedDto> source)
    : AppDbContextSeeder<RuleVersion, RuleVersionSeedDto>(db, source, logger)
{
    protected override async Task<RuleVersion?> MapAsync(RuleVersionSeedDto dto, CancellationToken ct = default)
    {
        var rule = await DbContext.Set<Rule>().FindAsync([dto.RuleId], ct);

        if (rule is null)
            return null;

        return new PersonalRuleVersion
        {
            Id = dto.Id,
            StartDate = dto.StartDate,
            Rule = rule
        };
    }
}
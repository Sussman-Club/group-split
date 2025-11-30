using System.Text.Json;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(RuleSeeder))]
[DependsOn(typeof(UserSeeder))]
public class RuleVersionSeeder(
    AppDbContext db,
    ILogger<RuleVersionSeeder> logger,
    ISeedDataSource<RuleVersionSeedDto> source)
    : AppDbContextSeeder<RuleVersion, RuleVersionSeedDto>(db, source, logger)
{
    protected override async Task<RuleVersion?> MapAsync(RuleVersionSeedDto dto, CancellationToken ct = default)
    {
        return dto.Type switch
        {
            RuleVersionType.Personal => await MapPersonalVersionRuleAsync(dto, ct),
            RuleVersionType.Percentage => await MapPercentageVersionRuleAsync(dto, ct),
            _ => null
        };
    }

    private async Task<PersonalRuleVersion?> MapPersonalVersionRuleAsync(RuleVersionSeedDto dto,
        CancellationToken ct = default)
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

    private async Task<PercentRuleVersion?> MapPercentageVersionRuleAsync(RuleVersionSeedDto dto,
        CancellationToken ct = default)
    {
        // Prefer strongly-typed percentages if available (via custom JSON converter),
        // otherwise fall back to ExtensionData for backward compatibility.
        PercentRuleUserSeedDto[]? percentages = null;

        if (dto is PercentRuleVersionSeedDto typed)
        {
            percentages = typed.Percentages;
        }
        else if (dto.ExtensionData.TryGetValue("Percentages", out var jsonElement))
        {
            percentages = jsonElement.Deserialize<PercentRuleUserSeedDto[]>();
        }

        if (percentages is null)
            return null;

        var sum = percentages.Sum(x => x.Percentage);

        const decimal epsilon = 10e-3M;
        if (sum + epsilon < 100 || sum - epsilon > 100)
        {
            logger.LogWarning("Percentages for RuleVersion {RuleVersionId} do not sum up to 100%", dto.Id);
            return null;
        }

        var rule = await DbContext.Set<Rule>().FindAsync([dto.RuleId], ct);
        if (rule is null)
            return null;

        var usersIds = percentages.Select(x => x.UserId);

        var users = await (
            from user in DbContext.Set<User>()
            where usersIds.Contains(user.Id)
            select user
        ).ToListAsync(cancellationToken: ct);

        if (users.Count != percentages.Length)
        {
            logger.LogWarning(
                "Some users in the percentage rule version do not exist. RuleVersion {RuleVersionId}",
                dto.Id);

            return null;
        }

        var percentageUser = percentages.Select(x => new PercentRuleUser
        {
            User = users.Single(u => u.Id == x.UserId),
            Percentage = (double)x.Percentage
        });

        var ruleVersion = new PercentRuleVersion
        {
            Id = dto.Id,
            StartDate = dto.StartDate,
            Rule = rule,
        };

        foreach (var x in percentageUser)
        {
            ruleVersion.RuleUsers.Add(x);
        }

        return ruleVersion;
    }
}
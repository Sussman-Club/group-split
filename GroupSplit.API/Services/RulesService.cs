using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IRuleService
{
    Task<IQueryable<RuleVersion>> List(CancellationToken cancellationToken = default);
    Task<IQueryable<RuleVersion>> Get(Guid ruleId, CancellationToken cancellationToken = default);
    Task<RuleVersion> Create(CreateRuleRequest request, CancellationToken ct = default);
    Task<RuleDetailsResponse> GetRuleDetails(Guid ruleId, CancellationToken ct);
    Task<RuleVersion> Update(Guid ruleId, UpdateRuleRequest request, CancellationToken ct = default);
    Task Delete(Guid ruleId, CancellationToken ct = default);
}

public class RuleService(IUserService userService, AppDbContext dbContext) : IRuleService
{
    public async Task<IQueryable<RuleVersion>> List(CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            where version.EndDateTime == null
            select version;

        return query;
    }

    public async Task<IQueryable<RuleVersion>> Get(Guid ruleId, CancellationToken cancellationToken = default)
    {
        return from g in await List(cancellationToken)
            where g.Rule.Id == ruleId
            select g;
    }

    public async Task<RuleVersion> Create(CreateRuleRequest request, CancellationToken ct = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var groupResult =
            await (from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                    where @group.Id == request.GroupId
                    select new
                    {
                        Group = @group,
                        Rule = @group.Rules.FirstOrDefault(r => r.Category == request.Category)
                    })
                .FirstOrDefaultAsync(ct);

        if (groupResult is not { Group: not null })
            throw new InvalidOperationException("Group does not exist.");

        if (groupResult.Rule is not null &&
            await dbContext.Entry(groupResult.Rule).Collection(r => r.Versions).Query()
                .AnyAsync(x => x.EndDateTime == null, ct))
            throw new InvalidOperationException("Group already has a rule with the same category.");

        var existingRule = groupResult.Rule;
        var version = await MapVersionAsync(request.Version, ct);

        if (existingRule is not null)
        {
            // Reactivate expired rule by adding a new version
            dbContext.Add(version);
            existingRule.Versions.Add(version);
        }
        else
        {
            var rule = new Rule
            {
                Id = Guid.NewGuid(),
                Group = groupResult.Group,
                Category = request.Category,
                Versions = { version }
            };

            dbContext.Add(rule);
        }

        await dbContext.SaveChangesAsync(ct);

        return version;
    }

    public async Task<RuleDetailsResponse> GetRuleDetails(Guid ruleId, CancellationToken ct)
    {
        var ruleVersion =
            await (from rv in dbContext.Set<RuleVersion>()
                    where rv.Rule.Id == ruleId
                    orderby rv.StartDateTime descending
                    select rv)
                .Include(rv => rv.Rule)
                .FirstOrDefaultAsync(ct);

        if (ruleVersion is PercentRuleVersion percentRuleVersion)
        {
            await dbContext.Entry(percentRuleVersion)
                .Collection(p => p.RuleUsers)
                .Query()
                .Include(ru => ru.User)
                .LoadAsync(ct);
        }

        if (ruleVersion is null)
            throw new InvalidOperationException("Rule does not exist.");

        return ruleVersion switch
        {
            PercentRuleVersion percent => new RuleDetailsResponse
            {
                RuleId = ruleVersion.Rule.Id,
                RuleVersionId = ruleVersion.Id,
                Category = ruleVersion.Rule.Category,
                Version = new PercentRuleVersionDto
                {
                    Percentages = percent.RuleUsers
                        .ToDictionary(
                            ru => ru.User.Id,
                            ru => (decimal)ru.Percentage
                        )
                }
            },
            PersonalRuleVersion => new RuleDetailsResponse
            {
                RuleId = ruleVersion.Rule.Id,
                RuleVersionId = ruleVersion.Id,
                Category = ruleVersion.Rule.Category,
                Version = new PersonalRuleVersionDto()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(ruleVersion))
        };
    }

    public async Task<RuleVersion> Update(Guid ruleId, UpdateRuleRequest request, CancellationToken ct = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var ruleResult =
            await (from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                    from rule in @group.Rules
                    where rule.Id == ruleId
                    select new
                    {
                        Rule = rule,
                        LatestVersion = rule.Versions.OrderByDescending(v => v.StartDateTime).FirstOrDefault(),
                        CategoryConflict = @group.Rules.Any(r => r.Id != ruleId && r.Category == request.Category)
                    })
                .FirstOrDefaultAsync(ct);

        if (ruleResult is not { Rule: not null })
            throw new InvalidOperationException("Rule does not exist.");

        if (ruleResult.CategoryConflict)
            throw new InvalidOperationException("Group already has a rule with this category.");

        var existingRule = ruleResult.Rule;
        var latestVersion = ruleResult.LatestVersion;

        existingRule.Category = request.Category;

        RuleVersion? newVersion = null;
        if (!VersionEquals(latestVersion, request.Version))
        {
            latestVersion.EndDateTime = DateTime.UtcNow;
            newVersion = await MapVersionAsync(request.Version, ct);
            dbContext.Add(newVersion);
            existingRule.Versions.Add(newVersion);
        }

        await dbContext.SaveChangesAsync(ct);

        return newVersion ?? latestVersion;
    }

    public async Task Delete(Guid ruleId, CancellationToken ct = default)
    {
        var version = await (
                from ruleVersion in await List(ct)
                where ruleVersion.Rule.Id == ruleId
                select ruleVersion)
            .FirstOrDefaultAsync(ct);

        if (version is null)
            throw new InvalidOperationException("Rule does not exist.");

        version.EndDateTime = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<RuleVersion> MapVersionAsync(RuleVersionDto dto, CancellationToken ct)
    {
        return dto switch
        {
            PersonalRuleVersionDto personal => MapPersonalVersion(personal),
            PercentRuleVersionDto percent => await MapPercentVersion(percent, ct),
            _ => throw new InvalidOperationException("Unknown rule version type.")
        };
    }

    private RuleVersion MapPersonalVersion(PersonalRuleVersionDto dto)
    {
        return new PersonalRuleVersion
        {
            StartDateTime = DateTime.UtcNow
        };
    }

    private async Task<RuleVersion> MapPercentVersion(PercentRuleVersionDto dto, CancellationToken ct)
    {
        var total = dto.Percentages.Values.Sum();

        const decimal epsilon = 10e-3M;
        if (total + epsilon < 100 || total - epsilon > 100)
            throw new InvalidOperationException("Percentages must sum to 100%.");

        var userIds = dto.Percentages.Keys.ToList();

        var users = await dbContext.Set<User>()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        if (users.Count != dto.Percentages.Count)
            throw new InvalidOperationException("Some users in the percentage rule do not exist.");

        var version = new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        foreach (var (userId, percent) in dto.Percentages)
        {
            if (percent is 0) continue;
            version.RuleUsers.Add(new PercentRuleUser
            {
                User = users.Single(u => u.Id == userId),
                Percentage = (double)percent
            });
        }

        return version;
    }

    private bool VersionEquals(RuleVersion current, RuleVersionDto incoming)
    {
        return current switch
        {
            PercentRuleVersion percentCurrent
                when incoming is PercentRuleVersionDto percentIncoming
                => PercentVersionsEqual(percentCurrent, percentIncoming),

            PersonalRuleVersion when incoming is PersonalRuleVersionDto
                => true, // Nothing can change

            _ => false // Different types = changed
        };
    }

    private bool PercentVersionsEqual(PercentRuleVersion current, PercentRuleVersionDto incoming)
    {
        if (current.RuleUsers.Count != incoming.Percentages.Count)
            return false;

        foreach (var ru in current.RuleUsers)
        {
            if (!incoming.Percentages.TryGetValue(ru.User.Id, out var percent))
                return false;

            if (Math.Abs(ru.Percentage - (double)percent) > 0.001)
                return false;
        }

        return true;
    }
}
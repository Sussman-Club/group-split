using GroupSplit.API.Services.RuleVersionHandlers;
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

public class RuleService(
    IUserService userService,
    AppDbContext dbContext,
    IRuleVersionHandler<RuleVersion, RuleVersionDto> ruleVersionHandler) : IRuleService
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

        var version = await ruleVersionHandler.CreateEntity(request.GroupId, request.Version, ct);

        if (existingRule is not null)
        {
            // Reactivate the expired rule by adding a new version
            version.Rule = existingRule;
            dbContext.Add(version);
        }
        else
        {
            var rule = new Rule
            {
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
                    select rv)
                .Include(rv => rv.Rule)
                .FirstOrDefaultAsync(ct);

        if (ruleVersion is null)
            throw new InvalidOperationException("Rule does not exist.");

        return await ruleVersionHandler.GetRuleDetails(ruleVersion, ct);
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
        if (!ruleVersionHandler.Equals(latestVersion, request.Version))
        {
            latestVersion.EndDateTime = DateTime.UtcNow;
            newVersion = await ruleVersionHandler.CreateEntity(existingRule.Group.Id, request.Version, ct);
            dbContext.Add(newVersion);
            existingRule.Versions.Add(newVersion);
        }

        await dbContext.SaveChangesAsync(ct);

        return newVersion ?? latestVersion;
    }

    public async Task Delete(Guid ruleId, CancellationToken ct = default)
    {
        var version = await (
                from ruleVersion in await Get(ruleId, ct)
                select ruleVersion)
            .FirstOrDefaultAsync(ct);

        if (version is null)
            throw new InvalidOperationException("Rule does not exist.");

        version.EndDateTime = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }
}
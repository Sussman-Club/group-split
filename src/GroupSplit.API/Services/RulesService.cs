using GroupSplit.API.Services.RuleVersionHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IRuleService
{
    Task<IQueryable<RuleVersion>> List(CancellationToken ct = default);
    Task<IQueryable<RuleVersion>> Get(Guid ruleId, CancellationToken ct = default);
    Task<RuleVersion> Create(CreateRuleRequest request, CancellationToken ct = default);
    Task<RuleDetailsResponse> GetRuleDetails(Guid ruleId, CancellationToken ct);
    Task Update(Guid ruleId, UpdateRuleRequest request, CancellationToken ct = default);
    Task Delete(Guid ruleId, CancellationToken ct = default);
}

public class RuleService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    IRuleVersionHandler ruleVersionHandler) : IRuleService
{
    public async Task<IQueryable<RuleVersion>> List(CancellationToken ct = default)
    {
        var currentUser = userContext.User;

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            where version.EndDateTime == null
            select version;

        return query;
    }

    public async Task<IQueryable<RuleVersion>> Get(Guid ruleId, CancellationToken ct = default)
    {
        return from g in await List(ct)
            where g.Rule.Id == ruleId
            select g;
    }

    public async Task<RuleVersion> Create(CreateRuleRequest request, CancellationToken ct = default)
    {
        var currentUser = userContext.User;

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

        var version = await ruleVersionHandler.ToEntity(request.GroupId, request.Version, ct);

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
        // Through Get, which is scoped to the caller's groups and already keeps only the
        // current version, rather than over the whole table. Reading straight from the set
        // handed any signed-in caller who knew a rule id the category and the split itself
        // — every member's percentage or share count against their user id.
        var ruleVersion = await (await Get(ruleId, ct))
            .Include(rv => rv.Rule)
            .FirstOrDefaultAsync(ct);

        // A rule in someone else's group takes this path too, and says the same thing a
        // missing one does: whether it exists is not the caller's to learn.
        if (ruleVersion is null)
            throw new InvalidOperationException("Rule does not exist.");

        var version = await ruleVersionHandler.ToDto(ruleVersion, ct);

        return new RuleDetailsResponse
        {
            RuleId = ruleVersion.Rule.Id,
            RuleVersionId = ruleVersion.Id,
            Category = ruleVersion.Rule.Category,
            Version = version
        };
    }

    public async Task Update(Guid ruleId, UpdateRuleRequest request, CancellationToken ct = default)
    {
        var currentUser = userContext.User;

        var result =
            await (from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                    from rule in @group.Rules
                    where rule.Id == ruleId
                    from version in rule.Versions
                    where version.EndDateTime == null
                    select new
                    {
                        GroupId = @group.Id,
                        Rule = rule,
                        LatestVersion = version,
                        CategoryConflict = @group.Rules.Any(r => r.Id != ruleId && r.Category == request.Category)
                    })
                .FirstOrDefaultAsync(ct);

        if (result is not { Rule: { } existingRule, LatestVersion: { } latestVersion })
            throw new InvalidOperationException("Rule does not exist.");

        if (result.CategoryConflict)
            throw new InvalidOperationException("Group already has a rule with this category.");
        
        if (!existingRule.IsEditable)
            throw new InvalidOperationException("Rule is not editable.");

        existingRule.Category = request.Category;

        if (!await ruleVersionHandler.Equals(latestVersion, request.Version, ct))
        {
            latestVersion.EndDateTime = DateTime.UtcNow;

            var newVersion = await ruleVersionHandler.ToEntity(result.GroupId, request.Version, ct);

            dbContext.Add(newVersion);
            existingRule.Versions.Add(newVersion);
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task Delete(Guid ruleId, CancellationToken ct = default)
    {
        var version = await (
                from ruleVersion in await Get(ruleId, ct)
                select ruleVersion)
            .Include(x => x.Rule)
            .FirstOrDefaultAsync(ct);

        if (version is null)
            throw new InvalidOperationException("Rule does not exist.");
        
        if (!version.Rule.IsDeletable)
            throw new InvalidOperationException("Rule is not deletable.");

        version.EndDateTime = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);
    }
}

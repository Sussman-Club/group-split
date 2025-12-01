using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IRuleService
{
    Task<IQueryable<RuleVersion>> List(CancellationToken cancellationToken = default);
    Task<IQueryable<RuleVersion>> Get(Guid id, CancellationToken cancellationToken = default);
    Task<RuleVersion> Create(CreateRuleRequest request, CancellationToken ct = default);
}

public class RuleService(IUserService userService, IGroupService groupService, AppDbContext dbContext) : IRuleService
{
    public async Task<IQueryable<RuleVersion>> List(CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            select version;

        return query;
    }

    public async Task<IQueryable<RuleVersion>> Get(Guid id, CancellationToken cancellationToken = default)
    {
        return from g in await List(cancellationToken)
            where g.Id == id
            select g;
    }

    public async Task<RuleVersion> Create(CreateRuleRequest request, CancellationToken ct = default)
    {
        var groupsQuery = await groupService.GetGroupById(request.GroupId, ct);

        var groupResult =
            await (from g in groupsQuery
                    select new
                    {
                        Group = g,
                        AlreadyHasRule = g.Rules.Any(r => r.Category == request.Category)
                    })
                .FirstOrDefaultAsync(ct);

        if (groupResult is not { Group: not null })
            throw new InvalidOperationException("Group does not exist.");

        if (groupResult.AlreadyHasRule)
            throw new InvalidOperationException("Group already has a rule with this category.");

        var version = await MapVersionAsync(request.Version, ct);

        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = groupResult.Group,
            Category = request.Category,
            Versions = { version }
        };

        dbContext.Add(rule);
        await dbContext.SaveChangesAsync(ct);

        return version;
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
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
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
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        foreach (var (userId, percent) in dto.Percentages)
        {
            version.RuleUsers.Add(new PercentRuleUser
            {
                User = users.Single(u => u.Id == userId),
                Percentage = (double)percent
            });
        }

        return version;
    }
}
using GroupSplit.API.Errors;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ISplitRuleService
{
    Task<IQueryable<SplitRule>> List(Guid? groupId, CancellationToken ct = default);
    Task<SplitRuleDetailsResponse> GetDetails(Guid id, CancellationToken ct = default);
    Task<SplitRule> Create(CreateSplitRuleRequest request, CancellationToken ct = default);
    Task<SplitRule> Update(Guid id, UpdateSplitRuleRequest request, CancellationToken ct = default);
    Task Delete(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The divisions a group keeps, to file its categories under.
/// </summary>
/// <remarks>
/// No versions. Editing a rule changes what the next expense is pre-filled with and nothing
/// already recorded, because each expense stores the amounts it was divided into -- which
/// is what the version chain was reaching for, and stronger, since it survives an edit
/// rather than merely dating it.
/// </remarks>
public class SplitRuleService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    ISplitRuleHandler handlers,
    ISplitRuleFactory factory) : ISplitRuleService
{
    public Task<IQueryable<SplitRule>> List(Guid? groupId, CancellationToken ct = default)
    {
        var groups = dbContext.Entry(userContext.User).Collection(u => u.Groups).Query();

        var query = from rule in dbContext.Set<SplitRule>()
                    where groups.Any(@group => @group.Id == rule.Group.Id)
                          && (groupId == null || rule.Group.Id == groupId)
                    select rule;

        return Task.FromResult(query);
    }

    public async Task<SplitRuleDetailsResponse> GetDetails(Guid id, CancellationToken ct = default)
    {
        var rule = await Existing(id, ct);

        return new SplitRuleDetailsResponse
        {
            Id = rule.Id,
            GroupId = rule.Group.Id,
            Name = rule.Name,
            Definition = handlers.ToDto(rule)
        };
    }

    public async Task<SplitRule> Create(CreateSplitRuleRequest request, CancellationToken ct = default)
    {
        var group = await GroupOfCaller(request.GroupId, ct);

        await RefuseDuplicateName(group.Id, request.Name, null, ct);

        var rule = factory.FromDto(request.Name, request.Definition);
        rule.Group = group;

        await Validate(rule, group, ct);

        dbContext.Add(rule);
        await dbContext.SaveChangesAsync(ct);

        return rule;
    }

    /// <summary>
    /// Replaces a rule's definition in place.
    /// </summary>
    /// <remarks>
    /// A definition of a different kind means a different type, and a row cannot change its
    /// type: the old rule is removed and a new one takes its place, with the categories that
    /// pointed at it moved across. Recorded expenses are untouched either way -- they hold
    /// their own splits.
    /// </remarks>
    public async Task<SplitRule> Update(Guid id, UpdateSplitRuleRequest request, CancellationToken ct = default)
    {
        var existing = await Existing(id, ct);

        await RefuseDuplicateName(existing.Group.Id, request.Name, id, ct);

        var replacement = factory.FromDto(request.Name, request.Definition);

        await Validate(replacement, existing.Group, ct);

        if (replacement.GetType() == existing.GetType() && existing is WeightedSplitRule weighted)
        {
            existing.Name = request.Name;

            dbContext.RemoveRange(weighted.Participants.ToList());
            weighted.Participants.Clear();

            foreach (var participant in ((WeightedSplitRule)replacement).Participants)
            {
                weighted.Participants.Add(new SplitRuleParticipant
                {
                    SplitRuleId = existing.Id,
                    UserId = participant.UserId,
                    Weight = participant.Weight
                });
            }

            await dbContext.SaveChangesAsync(ct);

            return existing;
        }

        replacement.Group = existing.Group;

        var pointingHere = await dbContext.Set<Category>()
            .Where(category => category.DefaultSplitRuleId == existing.Id)
            .ToListAsync(ct);

        dbContext.Add(replacement);

        foreach (var category in pointingHere)
            category.DefaultSplitRule = replacement;

        dbContext.Remove(existing);

        await dbContext.SaveChangesAsync(ct);

        return replacement;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var rule = await Existing(id, ct);

        var stillDefault = await dbContext.Set<Category>()
            .AnyAsync(category => category.DefaultSplitRuleId == id, ct);

        // Restrict rather than silently un-defaulting the categories: a rule several
        // categories point at is exactly the one somebody will try to delete, and emptying
        // their defaults would change how every future expense in them is split without
        // saying so.
        if (stillDefault)
            throw new ConflictException(ErrorCodes.SplitRuleInUse,
                "This rule is still the default for a category.");

        dbContext.Remove(rule);
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<SplitRule> Existing(Guid id, CancellationToken ct) =>
        await (await List(null, ct))
            .Include(rule => rule.Group)
            .Include(rule => (rule as WeightedSplitRule)!.Participants)
            .FirstOrDefaultAsync(rule => rule.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.SplitRuleNotFound, "Split rule not found.");

    private async Task<Group> GroupOfCaller(Guid groupId, CancellationToken ct) =>
        await dbContext.Entry(userContext.User).Collection(u => u.Groups).Query()
            .FirstOrDefaultAsync(@group => @group.Id == groupId, ct)
        ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

    /// <summary>
    /// What the kind itself says is wrong, and then the one thing no handler can know:
    /// whether the people it names are in the group.
    /// </summary>
    private async Task Validate(SplitRule rule, Group group, CancellationToken ct)
    {
        if (handlers.Invalid(rule) is { } complaint)
            throw new ValidationException(ErrorCodes.SplitRuleInvalid, complaint);

        if (rule is not WeightedSplitRule weighted || weighted.Participants.Count == 0)
            return;

        var named = weighted.Participants.Select(participant => participant.UserId).ToList();

        var inGroup = await dbContext.Entry(group).Collection(g => g.Users).Query()
            .Where(user => named.Contains(user.Id))
            .CountAsync(ct);

        if (inGroup != named.Count)
            throw new ValidationException(ErrorCodes.RuleUsersNotInGroup,
                "Some people in the rule are not members of the group.");
    }

    private async Task RefuseDuplicateName(Guid groupId, string name, Guid? excluding, CancellationToken ct)
    {
        var taken = await dbContext.Set<SplitRule>().AnyAsync(rule =>
            rule.Group.Id == groupId &&
            rule.Id != excluding &&
            rule.Name.ToLower() == name.ToLower(), ct);

        if (taken)
            throw new ConflictException(ErrorCodes.SplitRuleNameTaken,
                $"This group already has a rule called \"{name}\".");
    }
}

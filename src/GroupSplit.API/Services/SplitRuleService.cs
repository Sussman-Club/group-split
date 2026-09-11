using GroupSplit.API.Errors;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ISplitRuleService
{
    Task<IQueryable<SplitRule>> List(Guid? groupId, CancellationToken ct = default);
    Task<SplitRuleDetailsResponse> GetDetails(Guid id, CancellationToken ct = default);

    /// <summary>Every division the rule has stood for, newest first.</summary>
    Task<SplitRuleHistoryResponse> GetHistory(Guid id, CancellationToken ct = default);

    Task<SplitRule> Create(CreateSplitRuleRequest request, CancellationToken ct = default);
    Task<SplitRule> Update(Guid id, UpdateSplitRuleRequest request, CancellationToken ct = default);

    /// <summary>
    /// Writes the divisions a rule stood for before it was recorded here, oldest first.
    /// </summary>
    /// <remarks>
    /// For a rule whose past happened somewhere else. The ordinary edit is the only other way
    /// a version is ever opened and it stamps the clock, so a rule imported from a workbook of
    /// 42 months arrives holding one version and no way to say it ever said anything else --
    /// which makes every expense migrated with it point at a division that was not in force
    /// when it was spent.
    /// </remarks>
    /// <exception cref="ValidationException">
    /// No entries, dates that do not strictly increase, an entry starting after now, or a
    /// definition that is not coherent or names somebody outside the group.
    /// </exception>
    /// <exception cref="ConflictException">
    /// The rule has been through more than one version already, or the last entry is not the
    /// division the rule stands for now.
    /// </exception>
    Task<SplitRuleHistoryResponse> SetHistory(
        Guid id, IReadOnlyList<SplitRuleVersionInput> versions, CancellationToken ct = default);

    Task Delete(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The divisions a group keeps, for its categories to point at.
/// </summary>
/// <remarks>
/// A rule is a name and a chain of versions, and this service only ever adds to the chain.
/// Editing a rule's definition closes the version that was current and opens a new one, so
/// the row every expense already recorded points at says tomorrow what it said the day the
/// expense was written -- which is what makes "divide this again by the rule it had at the
/// time" answerable at all.
/// <para>
/// Three edits, three effects, none of them reaching the others: renaming a rule touches no
/// version, re-pointing a category touches no rule, and editing a definition touches no
/// category and no recorded expense.
/// </para>
/// </remarks>
public class SplitRuleService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    ISplitRuleHandler handlers,
    ISplitRuleFactory factory,
    IGroupParticipants participants) : ISplitRuleService
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
        var current = CurrentOf(rule);

        return new SplitRuleDetailsResponse
        {
            Id = rule.Id,
            GroupId = rule.Group.Id,
            Name = rule.Name,
            VersionId = current.Id,
            ChangedAt = current.StartedAt,
            Definition = handlers.ToDto(current)
        };
    }

    public async Task<SplitRuleHistoryResponse> GetHistory(Guid id, CancellationToken ct = default)
    {
        var rule = await Existing(id, ct);

        // Newest first, and the current one is the newest: a version is only ever opened
        // when the one before it is closed, so StartedAt orders the chain.
        var versions = rule.Versions
            .OrderByDescending(version => version.StartedAt)
            .ThenByDescending(version => version.SupersededAt is null)
            .Select(version => new SplitRuleVersionResponse(
                version.Id, version.StartedAt, version.SupersededAt, handlers.ToDto(version)))
            .ToList();

        return new SplitRuleHistoryResponse(rule.Id, rule.Group.Id, rule.Name, versions);
    }

    public async Task<SplitRule> Create(CreateSplitRuleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var group = await GroupOfCaller(request.GroupId, ct);

        await RefuseDuplicateName(group.Id, request.Name, null, ct);

        var version = factory.FromDto(request.Definition);

        await Validate(version, group, ct);

        var rule = new SplitRule
        {
            Group = group,
            Name = request.Name,
            Versions = { version }
        };

        dbContext.Add(rule);
        await dbContext.SaveChangesAsync(ct);

        return rule;
    }

    /// <summary>
    /// Renames the rule, and -- when the definition is a different division from the one it
    /// stands for now -- closes the current version and opens a new one.
    /// </summary>
    /// <remarks>
    /// Nothing is overwritten and nothing is deleted, which is the difference from replacing
    /// a rule in place. A definition of a different kind is a different type and could not
    /// have been edited in place anyway; now it does not need to be, because a new version
    /// is a new row whatever kind it is, and the categories pointing at the rule keep
    /// pointing at the rule.
    /// <para>
    /// An edit that says the same thing writes no version. Otherwise every save from a
    /// dialog that round-trips the definition would add a row, and a rule's history would
    /// stop being a list of the times it changed.
    /// </para>
    /// </remarks>
    public async Task<SplitRule> Update(Guid id, UpdateSplitRuleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await Existing(id, ct);

        await RefuseDuplicateName(rule.Group.Id, request.Name, id, ct);

        var proposed = factory.FromDto(request.Definition);

        await Validate(proposed, rule.Group, ct);

        rule.Name = request.Name;

        var current = CurrentOf(rule);

        if (!handlers.SameAs(current, proposed))
        {
            var now = DateTimeOffset.UtcNow;

            current.SupersededAt = now;
            proposed.StartedAt = now;
            proposed.SplitRule = rule;

            // Added explicitly rather than by hanging it off the tracked rule. Ids here are
            // client-generated, so EF meets a fresh version with a key already set, decides
            // it must be an existing row to UPDATE, and fails on save against a row that was
            // never there. The same trap ExpenseSplitter meets with splits.
            dbContext.Add(proposed);
        }

        await dbContext.SaveChangesAsync(ct);

        return rule;
    }

    /// <inheritdoc />
    public async Task<SplitRuleHistoryResponse> SetHistory(
        Guid id, IReadOnlyList<SplitRuleVersionInput> versions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versions);

        if (versions.Count == 0)
            throw new ValidationException(ErrorCodes.SplitRuleHistoryInvalid,
                "A history needs at least one entry: the division the rule stands for now.");

        // Strictly increasing, because each entry's window ends where the next one begins. Two
        // sharing a date would leave a version that was never what the rule said, and one
        // going backwards a version that ended before it started -- and neither can be told
        // apart afterwards from a rule that really did change twice in a second.
        for (var i = 1; i < versions.Count; i++)
        {
            if (versions[i].From > versions[i - 1].From)
                continue;

            throw new ValidationException(ErrorCodes.SplitRuleHistoryInvalid,
                    "The entries have to be in order, oldest first, with no two starting at " +
                    "the same moment.")
                .WithExtension("index", i)
                .WithExtension("from", versions[i].From)
                .WithExtension("previousFrom", versions[i - 1].From);
        }

        // A history is an account of what the rule has already said, so nothing in it may
        // start after now -- and the last entry least of all, because it becomes the open
        // version. Two readers would then disagree about today's spending: ExpenseSplitter
        // reaches for the version nothing has superseded and would divide a new expense by a
        // window that has not opened, while ExpenseProvenance.Covering looks for the window
        // containing the date and would reattach the very same expense to the entry before
        // it. Only the last is checked because the entries strictly increase above, so it is
        // the latest of them.
        if (versions[^1].From > DateTimeOffset.UtcNow)
            throw new ValidationException(ErrorCodes.SplitRuleHistoryInvalid,
                    "A history says what a rule has already stood for, so no entry can start " +
                    "in the future.")
                .WithExtension("index", versions.Count - 1)
                .WithExtension("from", versions[^1].From);

        var rule = await Existing(id, ct);

        // One version is the state a freshly created rule is in, and the state the migration
        // left every rule in. A rule that has changed since has windows of its own and
        // expenses pointing into them, and deciding what a written history does to those is a
        // larger question than this answers -- so it is refused by name rather than guessed
        // at.
        if (rule.Versions.Count > 1)
            throw new ConflictException(ErrorCodes.SplitRuleAlreadyHasHistory,
                    $"\"{rule.Name}\" has already been through {rule.Versions.Count} versions, " +
                    "so its history cannot be written in wholesale.")
                .WithExtension("splitRuleId", rule.Id)
                .WithExtension("versionCount", rule.Versions.Count);

        var current = CurrentOf(rule);

        var proposed = new List<SplitRuleVersion>(versions.Count);

        foreach (var entry in versions)
        {
            var version = factory.FromDto(entry.Definition);
            await Validate(version, rule.Group, ct);
            proposed.Add(version);
        }

        // The last entry is the rule as it stands, and has to be: it becomes the open
        // version, and the open version is the row every already-recorded expense points at.
        // Checked by what the division says rather than by what the DTO looks like, which is
        // the handler's judgement and the same one an edit uses to decide whether anything
        // changed at all.
        if (!handlers.SameAs(current, proposed[^1]))
            throw new ConflictException(ErrorCodes.SplitRuleHistoryEndsElsewhere,
                    $"The last entry is not the division \"{rule.Name}\" stands for now. Correct " +
                    "the rule with an ordinary update first, then write its history.")
                .WithExtension("splitRuleId", rule.Id)
                .WithExtension("currentVersionId", current.Id);

        // Reused, not replaced. Deleting it and writing the same division back would orphan
        // every transaction that records it -- the pointer is the only account of which
        // division produced those amounts -- so the one thing that moves on this row is the
        // date it opened.
        current.StartedAt = versions[^1].From;
        current.SupersededAt = null;

        for (var i = 0; i < proposed.Count - 1; i++)
        {
            proposed[i].SplitRuleId = rule.Id;
            proposed[i].StartedAt = versions[i].From;
            proposed[i].SupersededAt = versions[i + 1].From;

            // Added explicitly, for the reason Update gives: an id is already set on a fresh
            // version, so EF hanging it off the tracked rule would take it for an UPDATE.
            dbContext.Add(proposed[i]);
        }

        await dbContext.SaveChangesAsync(ct);

        return await GetHistory(rule.Id, ct);
    }

    /// <summary>
    /// Deletes a rule and the versions it has been through, once nothing needs either.
    /// </summary>
    /// <remarks>
    /// Refused twice over, for two different reasons. A category still defaulting to it is a
    /// decision about the future: emptying its default would change how every expense filed
    /// there from now on is divided, without saying so. A transaction divided by one of its
    /// versions is a fact about the past: the version is the only record of which division
    /// produced that expense's amounts, so deleting it would leave an expense that cannot be
    /// recalculated and cannot say why.
    /// </remarks>
    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var rule = await Existing(id, ct);

        var stillDefault = await dbContext.Set<Category>()
            .AnyAsync(category => category.DefaultSplitRuleId == id, ct);

        if (stillDefault)
            throw new ConflictException(ErrorCodes.SplitRuleInUse,
                "This rule is still the default for a category.");

        var everUsed = await dbContext.Set<Transaction>()
            .AnyAsync(transaction => transaction.SplitRuleVersion!.SplitRuleId == id, ct);

        if (everUsed)
            throw new ConflictException(ErrorCodes.SplitRuleInUse,
                "Expenses have been divided by this rule, so it is part of their history and cannot be deleted.");

        dbContext.Remove(rule);
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<SplitRule> Existing(Guid id, CancellationToken ct) =>
        await (await List(null, ct))
            .Include(rule => rule.Group)
            .Include(rule => rule.Versions)
            .ThenInclude(version => (version as WeightedSplitRuleVersion)!.Participants)
            .FirstOrDefaultAsync(rule => rule.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.SplitRuleNotFound, "Split rule not found.");

    /// <summary>
    /// What the rule says now. A rule without one is a rule whose chain was broken by
    /// something outside this service, which is a defect and not a refusal: every path that
    /// opens a version closes the one before it in the same save, and the database carries a
    /// partial unique index saying at most one can be open.
    /// </summary>
    private static SplitRuleVersion CurrentOf(SplitRule rule) =>
        rule.Current ?? throw new InvalidOperationException(
            $"Split rule {rule.Id} has no current version.");

    private async Task<Group> GroupOfCaller(Guid groupId, CancellationToken ct) =>
        await dbContext.Entry(userContext.User).Collection(u => u.Groups).Query()
            .FirstOrDefaultAsync(@group => @group.Id == groupId, ct)
        ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

    /// <summary>
    /// What the kind itself says is wrong, and then the one thing no handler can know:
    /// whether the people it names belong to the group.
    /// </summary>
    /// <remarks>
    /// Participants and not members, so a rule may name somebody the group has invited and
    /// is waiting on. A rule is the template the next expense is divided by, and the next
    /// expense is exactly the one an invitee needs a share of -- the trip that prompted the
    /// invitation. If they never join, the rule stops naming them, the same way it stops
    /// naming a member who leaves.
    /// </remarks>
    private async Task Validate(SplitRuleVersion ruleVersion, Group group, CancellationToken ct)
    {
        if (handlers.Invalid(ruleVersion) is { } complaint)
            throw new ValidationException(ErrorCodes.SplitRuleInvalid, complaint);

        if (ruleVersion is not WeightedSplitRuleVersion weighted || weighted.Participants.Count == 0)
            return;

        var named = weighted.Participants.Select(participant => participant.UserId).ToList();

        var known = await participants.Of(group.Id)
            .Where(user => named.Contains(user.Id))
            .CountAsync(ct);

        if (known != named.Count)
            throw new ValidationException(ErrorCodes.RuleUsersNotInGroup,
                "Some people in the rule are neither members of the group nor invited to it.");
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

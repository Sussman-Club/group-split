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
            Definition = handlers.ToDto(current),
            BuiltIn = rule.BuiltIn
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

        // One per group, and the group was given it. A second would divide identically --
        // this rule has no settings to differ by -- so it would be a second name for the
        // same thing, and every client offering "divide it by the bill" would then have to
        // guess which one an expense meant.
        await RefuseASecondBillRule(version, group, ct);

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

        RefuseIfProvisioned(rule, "restated or renamed");

        await RefuseDuplicateName(rule.Group.Id, request.Name, id, ct);

        var proposed = factory.FromDto(request.Definition);

        await Validate(proposed, rule.Group, ct);

        var current = CurrentOf(rule);

        RefuseADifferentKind(rule, current, proposed);

        rule.Name = request.Name;

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
        // version. ExpenseSplitter reaches for the version nothing has superseded, whatever
        // the date on it, so a forward-dated entry is one the app divides by while the
        // history says it has not started: the rule's own account of itself and today's
        // spending would disagree. Only the last is checked because the entries strictly
        // increase above, so it is the latest of them.
        if (versions[^1].From > DateTimeOffset.UtcNow)
            throw new ValidationException(ErrorCodes.SplitRuleHistoryInvalid,
                    "A history says what a rule has already stood for, so no entry can start " +
                    "in the future.")
                .WithExtension("index", versions.Count - 1)
                .WithExtension("from", versions[^1].From);

        var rule = await Existing(id, ct);

        RefuseIfProvisioned(rule, "given a history");

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

        RefuseIfProvisioned(rule, "deleted");

        var stillDefault = await dbContext.Set<Category>()
            .AnyAsync(category => category.DefaultSplitRuleId == id, ct);

        if (stillDefault)
            throw new ConflictException(ErrorCodes.SplitRuleInUse,
                "This rule is still the default for a category.");

        var everUsed = await dbContext.Set<Transaction>()
            .AnyAsync(transaction => transaction.SplitRuleVersion!.SplitRuleId == id, ct);

        if (everUsed || await dbContext.Set<ReceiptItem>()
            .AnyAsync(item => item.SplitRuleVersion!.SplitRuleId == id, ct))
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

    /// <summary>
    /// Refuses anything that would change a rule the group was given rather than wrote.
    /// </summary>
    /// <remarks>
    /// One sentence, three callers, and the same answer to all of them: these say one thing
    /// and go on saying it. That is not tidiness -- an expense can name one of these without
    /// anybody having created it, so what they mean has to be fixed, or a restatement would
    /// move money on expenses recorded under a division somebody never chose. A group that
    /// wants "all of it is for Ana, until we say otherwise" creates a rule of its own, and
    /// that one is editable like any other.
    /// </remarks>
    /// <summary>
    /// Refuses a division of a different shape from the one the rule stands for.
    /// </summary>
    /// <remarks>
    /// A rule is a named division a group refers to, and the shape of that division is part
    /// of what the rule is -- not a field on it. "Household 3-way" turning into "all on
    /// whoever paid" is not an edit anybody makes on purpose: every category pointing at it
    /// and every expense divided by it was pointed at a rule that divided in proportion, and
    /// the next expense under any of them would suddenly not be. Changing the numbers is an
    /// edit; changing the shape is a different rule wearing the name, and making one is a
    /// line of the same API.
    /// <para>
    /// Percentages and shares are two shapes, not one, for the same reason: the numbers a
    /// member reads off the rule mean different things, and a percentage rule that no longer
    /// totals 100 is refused where the same weights as shares are perfectly good. The editor
    /// converts between them while a rule is being <em>written</em>, which is where choosing
    /// the shape belongs.
    /// </para>
    /// <para>
    /// Asked of an edit only, and not of a written history. An edit is a decision somebody is
    /// making now, and this is what they are told they cannot decide; a history is an account
    /// of a past that already happened somewhere else, and a flat that really did divide its
    /// rent evenly until it started keeping shares has to be able to say so. What guards that
    /// path is the entry it ends on: the last one has to be the division the rule stands for
    /// now, kind included.
    /// </para>
    /// </remarks>
    private static void RefuseADifferentKind(
        SplitRule rule, SplitRuleVersion current, SplitRuleVersion proposed)
    {
        if (current.GetType() == proposed.GetType())
            return;

        throw new ConflictException(ErrorCodes.SplitRuleKindFixed,
                $"\"{rule.Name}\" divides one way and this would make it divide another. A "
                + "rule keeps the shape it was written with; create a rule for the new one.")
            .WithExtension("splitRuleId", rule.Id);
    }

    /// <summary>
    /// Refuses a second "divide it by the bill" rule, naming the one the group already has.
    /// </summary>
    /// <remarks>
    /// The group is given one when it is created, so in practice this always has one to point
    /// at; it is written to survive a group that somehow has none rather than to report a
    /// state the API can reach.
    /// </remarks>
    private async Task RefuseASecondBillRule(SplitRuleVersion version, Group group, CancellationToken ct)
    {
        if (version is not ItemizedSplitRuleVersion)
            return;

        var held = await dbContext.Set<SplitRule>()
            .FirstOrDefaultAsync(rule =>
                rule.Group.Id == group.Id &&
                rule.Versions.Any(open =>
                    open.SupersededAt == null && open is ItemizedSplitRuleVersion), ct);

        if (held is null)
            return;

        throw new ConflictException(ErrorCodes.SplitRuleBillIsProvisioned,
                $"This group already divides bills by \"{held.Name}\", which it was given and "
                + "keeps. A bill is divided by the rule on each of its lines, so a second of "
                + "these would divide exactly the same way under another name.")
            .WithExtension("splitRuleId", held.Id);
    }

    private static void RefuseIfProvisioned(SplitRule rule, string what)
    {
        if (!rule.BuiltIn)
            return;

        // Two kinds of provisioned rule now, and they are held for different reasons: saying
        // "for one of its members" about the bill rule would be simply untrue.
        var why = rule.Versions.Any(version => version.SupersededAt is null && version is ItemizedSplitRuleVersion)
            ? "is the rule this group holds for dividing an expense by its own bill"
            : "is the rule this group holds for one of its members";

        throw new ConflictException(ErrorCodes.SplitRuleNotEditable,
                $"\"{rule.Name}\" {why}, so it cannot be {what}. Create a rule of your own to "
                + "divide differently.")
            .WithExtension("splitRuleId", rule.Id)
            // Read off the division rather than off the rule, because that is where it is
            // said. A caller that wants to name the person in a message has it already.
            .WithExtension("allForUserId", rule.AllFor);
    }

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

        // Who a rule names is asked of the rule's shape rather than of one interface, because
        // the shapes are the point: a proportional rule names a list, and the one that puts
        // the whole amount on somebody names exactly one person -- who has to be in the group
        // for the same reason and with the same words.
        List<Guid> named = ruleVersion switch
        {
            WeightedSplitRuleVersion weighted =>
                weighted.Participants.Select(participant => participant.UserId).ToList(),
            SoleSplitRuleVersion sole => [sole.UserId],
            _ => []
        };

        if (named.Count == 0)
            return;

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

using GroupSplit.API.Errors;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IExpenseSplitter
{
    /// <summary>Divides the expense the way its category says.</summary>
    Task WriteSplitsAsync(Expense expense, CancellationToken ct = default);

    /// <summary>
    /// Divides the expense as <paramref name="given"/> states, or the way its category
    /// says when that is null.
    /// </summary>
    Task WriteSplitsAsync(Expense expense, IReadOnlyList<SplitInput>? given, CancellationToken ct = default);

    /// <summary>
    /// Whether this expense would be divided by the bill attached to it.
    /// </summary>
    /// <remarks>
    /// Asked of the version the expense would actually be divided by -- the one it was
    /// written under when that still belongs to its category's rule, and otherwise the
    /// rule's current one. That distinction is the whole reason this lives here rather than
    /// being worked out by the caller: a rule edited from even to itemised leaves an older
    /// expense still pointing at the even version, so "the rule is itemised now" and "this
    /// expense divides by its bill" are different questions with different answers, and
    /// answering the first while acting on the second stores an even split and reports an
    /// itemised one.
    /// </remarks>
    Task<bool> DividesByItsBill(Expense expense, CancellationToken ct = default);

    /// <summary>
    /// Whether the shares the expense holds are the ones its own rule produces -- that is,
    /// whether a rule divided it or a person did.
    /// </summary>
    /// <remarks>
    /// The model does not record which, and cannot: the endpoint carries stored shares into
    /// every edit, so "stated" arrives for a rename as readily as for a hand-typed split,
    /// and the version an expense points at may have been guessed by the migration that
    /// introduced versions. Re-running the division and comparing is the one answer that
    /// does not depend on either.
    /// <para>
    /// Asked <em>before</em> an edit is applied, while the expense still holds the amount
    /// and the payer its shares were worked out from. Afterwards the question cannot be
    /// asked at all.
    /// </para>
    /// </remarks>
    Task<bool> DivisionCameFromItsRule(Expense expense, CancellationToken ct = default);
}

/// <summary>
/// Works out what each person owed on an expense, and stores it.
/// </summary>
/// <remarks>
/// The one place a split is decided. It runs on create and on every edit, because the
/// amount, the payer and the category each change what everybody owed -- and a stored split
/// that no longer sums to its amount makes every balance in the group wrong with nothing
/// else to catch it.
/// <para>
/// Two ways in, and the difference is who decided. Given nothing, it divides by the
/// category's rule, which is what nearly every expense wants. Given amounts, it stores
/// those -- after checking the one thing that cannot be checked anywhere else, that they
/// sum to the amount they claim to divide.
/// </para>
/// </remarks>
public class ExpenseSplitter(
    AppDbContext dbContext,
    ISplitRuleHandler splitRules,
    IGroupParticipants participants) : IExpenseSplitter
{
    public Task WriteSplitsAsync(Expense expense, CancellationToken ct = default) =>
        WriteSplitsAsync(expense, given: null, ct);

    public async Task WriteSplitsAsync(Expense expense, IReadOnlyList<SplitInput>? given,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        var payerId = expense.Payer;

        // A personal expense has nobody to divide with, so there is nothing a stated
        // division could say that is not either "all of it, to me" -- which is what it gets
        // anyway -- or a share for somebody who cannot see the expense at all. Refused by
        // name rather than left to fall out of the membership check below, which would
        // report a group the expense does not have.
        if (given is not null && (expense.Group?.Id ?? expense.GroupId) is null)
            throw new ValidationException(ErrorCodes.SplitOnAPersonalExpense,
                "A personal expense is not shared with anybody, so it cannot be split.");

        var members = await MembersOf(expense, ct);

        if (given is not null)
        {
            var stated = AsStated(given, expense.Amount, members);

            // Amounts somebody typed are their own record: there is no rule behind them,
            // and claiming one would make a later edit re-divide by a division nobody
            // chose. Cleared rather than left, because an expense that had a rule and now
            // has stated shares no longer has one.
            //
            // Only when they actually say something different, though. Since 47c6904 the
            // PATCH endpoint carries the expense's stored shares into every edit that says
            // nothing about them -- which is what keeps a rename from re-dividing it -- so
            // a division arrives stated for the ordinary rename as much as for the split
            // somebody typed, and this cannot tell the two apart by looking at it. What it
            // can tell is whether the division changed: amounts identical to the stored
            // ones are not a new division, so they are not a new answer to where the
            // division came from either. Clearing on those erased the version the first
            // time anybody edited a name, and the version is the only record of which rule
            // divided the expense.
            if (!IsWhatItAlreadyHolds(expense, stated))
            {
                expense.SplitRuleVersion = null;
                expense.SplitRuleVersionId = null;
            }

            Replace(expense, stated, members);
            return;
        }

        Replace(expense, await DividedByRule(expense, payerId, members, ct), members);
    }

    /// <summary>
    /// The division the expense's rule calls for -- see <see cref="VersionFor"/> for which
    /// version of it -- or an even one when it has no category or the category names no
    /// rule. Records the version on the expense either way, including as null.
    /// </summary>
    private async Task<IReadOnlyList<SplitAmount>> DividedByRule(
        Expense expense, Guid payerId, IReadOnlyCollection<Guid> members, CancellationToken ct)
    {
        var version = await VersionFor(expense, ct);

        expense.SplitRuleVersion = version;
        expense.SplitRuleVersionId = version?.Id;

        // No category, or a category that names no rule: evenly between the members. That
        // is the whole of what a group with no rules used to be unable to do.
        if (version is null)
            return SplitCalculator.DivideEvenly(expense.Amount, payerId, members);

        var rule = version.SplitRule;

        await LoadBillIfItDividesByOne(expense, version, ct);

        try
        {
            return splitRules.Divide(version, expense, members);
        }
        // Narrowed away from ArgumentNullException, which derives from this and means
        // something else entirely: SplitCalculator.Divide opens by refusing a null weight
        // list, and a handler that produced one is a defect. Reframed as a refusal it would
        // blame a well-formed rule, tell somebody to go and edit it, and keep the fault out
        // of the logs.
        catch (ArgumentException exception) when (exception is not ArgumentNullException)
        {
            // A rule can be left with nothing to divide by. Everybody it named has gone --
            // a member who left, or somebody invited whose invitation was declined or
            // withdrawn -- and both open a version that does not name them rather than
            // zeroing them, so a shares rule that named one person keeps none. It can also
            // be an older version that *does* still name them, which is exactly what an old
            // expense is divided by when it is edited: the version is history and goes on
            // saying what it said, but the division only pays people who are still
            // participants, so a version naming nobody who is left comes to the same empty
            // hand. SplitCalculator says so by throwing, which is right of it: no
            // participants, or weights summing to zero, is not a division it could carry
            // out.
            //
            // What was wrong was where that surfaced. An ArgumentException is nobody's
            // domain error, so it reached the client as a 500 with a trace id, and the
            // person it happened to was somebody recording a dinner. The rule really is
            // unusable and saying which one is the useful half of the answer.
            throw new ValidationException(ErrorCodes.SplitRuleInvalid,
                    $"\"{rule.Name}\" no longer divides between anybody, so an expense filed " +
                    "under this category cannot be split by it. Edit the rule, or file the " +
                    "expense under nothing to divide it evenly.")
                // The rule, and not the exception's own words. docs/errors.md keeps
                // exception text out of a response, and moving it from detail into an
                // extension member would be keeping the letter and losing the point:
                // SplitCalculator's guard clauses would become part of the API's observable
                // surface, so rewording one would be a client-visible change nobody had
                // thought about. Nothing is lost by dropping it -- it says either "no
                // participants" or "weights sum to zero", and the sentence above already
                // says the rule divides between nobody. What a caller can act on is which
                // rule it was.
                .WithExtension("splitRuleId", rule.Id)
                .WithExtension("splitRuleName", rule.Name);
        }
    }

    /// <summary>
    /// Whether the stated division is the one the expense already holds: the same people,
    /// for the same amounts.
    /// </summary>
    /// <remarks>
    /// Not "is this what the rule would give". Two divisions that agree to the cent are the
    /// same division whoever worked them out, and the only question here is whether this
    /// one is a change -- because a division that did not change cannot have changed where
    /// it came from.
    /// </remarks>
    public async Task<bool> DivisionCameFromItsRule(Expense expense, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        if (expense.Splits.Count == 0)
            return false;

        var payerId = expense.Payer;
        var members = await MembersOf(expense, ct);
        var version = await HeldVersion(expense, ct);

        if (version is not null)
            await LoadBillIfItDividesByOne(expense, version, ct);

        IReadOnlyList<SplitAmount> wouldBe;

        try
        {
            // No version is not "no rule ran". An expense under no category, or under one
            // naming no rule, was divided evenly -- which is a division the app made and
            // not one a person typed, and it has to follow the amount the same way.
            wouldBe = version is null
                ? SplitCalculator.DivideEvenly(expense.Amount, payerId, members)
                : splitRules.Divide(version, expense, members);
        }
        // A version that cannot divide right now explains nothing, so the shares are the
        // expense's own as far as this is concerned. Refusing here would turn "which of the
        // two is it" into an error on a path that only wanted to know whether to leave the
        // shares alone.
        catch (ArgumentException)
        {
            // A weighted rule left naming nobody: everybody it named has gone.
            return false;
        }
        catch (UnprocessableException)
        {
            // An itemised rule with no bill to read, one whose lines are not all claimed yet,
            // or one whose total no longer matches the expense. All are ordinary states of an
            // expense somebody is still working on, and this is asked on the way into *every*
            // edit -- so letting one escape would make renaming a half-finished dinner fail
            // with a complaint about its receipt.
            return false;
        }
        catch (ValidationException)
        {
            // The same thing said with a different status. A bill carrying a claim with no
            // share in it is refused as a 400, because that code answers 400 everywhere else
            // -- and the reclassification quietly took it out of the arm above, since these
            // are siblings rather than one deriving from the other. Renaming such an expense
            // started failing with RECEIPT_INVALID.
            return false;
        }

        return IsWhatItAlreadyHolds(expense, wouldBe);
    }

    /// <summary>
    /// Puts the expense's bill on it, for a version that divides by one.
    /// </summary>
    /// <remarks>
    /// Here rather than in the callers, for the reason <see cref="HeldVersion"/> re-reads a
    /// version it was handed: a handler is given whatever the caller loaded, and there are
    /// several callers. An itemised division reads <see cref="Expense.Receipt"/>, so an
    /// expense that reached this without one -- which is every expense TransactionService
    /// loads, since nothing else needs it -- would divide as though it had no bill and be
    /// refused for not having one.
    /// <para>
    /// Only for the kind that needs it. Loading a receipt and its lines and their claims on
    /// every division would be three joins bought for the rules that never look at them.
    /// </para>
    /// <para>
    /// By id rather than through the navigation, and skipped when the navigation is already
    /// filled: a caller that did include the bill has the tracked instance, and re-reading
    /// would not improve on it.
    /// </para>
    /// </remarks>
    private async Task LoadBillIfItDividesByOne(
        Expense expense, SplitRuleVersion version, CancellationToken ct)
    {
        if (version is not ItemizedSplitRuleVersion || expense.Bill is not null)
            return;

        // The whole bill, found by the lines that name this expense -- not just those lines.
        // How much of the tax and the tip is this part's depends on what the other parts of
        // the same charge hold, so a receipt loaded with half its items would divide a
        // warehouse run as though the jacket had never been on it.
        expense.Bill = await dbContext.Set<Receipt>()
            .Include(receipt => receipt.Items)
            .ThenInclude(item => item.Claims)
            .FirstOrDefaultAsync(
                receipt => receipt.Items.Any(item => item.ExpenseId == expense.Id), ct);
    }

    public async Task<bool> DividesByItsBill(Expense expense, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        return await VersionFor(expense, ct) is ItemizedSplitRuleVersion;
    }

    /// <summary>The version the expense was written under, loaded if it is not already.</summary>
    private async Task<SplitRuleVersion?> HeldVersion(Expense expense, CancellationToken ct)
    {
        // Read by id even when the navigation is loaded, because a loaded version is not
        // necessarily a divisible one: a weighted version divides by its participants, and a
        // caller that included the version without them hands over a rule that appears to
        // name nobody -- which this would read as "no rule could have produced these shares"
        // and answer false to, on exactly the expenses where the answer matters most.
        if (expense.SplitRuleVersionId is { } id)
            return await Versions().FirstOrDefaultAsync(version => version.Id == id, ct);

        return expense.SplitRuleVersion;
    }

    private static bool IsWhatItAlreadyHolds(Expense expense, IReadOnlyList<SplitAmount> stated)
    {
        if (expense.Splits.Count != stated.Count)
            return false;

        // A share at a time rather than through a dictionary keyed by member: the ids in
        // stated are distinct by the time this runs, but the stored rows are whatever is in
        // the table, and a defect that put a member in twice would turn this check into a
        // 500 on an edit rather than a false.
        return stated.All(share => expense.Splits.Any(stored =>
            stored.UserId == share.UserId && stored.Amount == share.Amount));
    }

    /// <summary>
    /// The two things that must be true of any division before it is stored, whoever worked
    /// it out: it sums to the expense's amount, and every share belongs to somebody the
    /// expense can be divided between.
    /// </summary>
    /// <remarks>
    /// Here rather than in <see cref="AsStated"/>, which is where both used to live, and the
    /// move is the point. <see cref="AsStated"/> runs on one path -- amounts a caller sent --
    /// so a handler that divided by something other than the expense's amount, or that named
    /// somebody who is no longer a participant, reached the table unchecked. Every kind that
    /// came before happened not to: they divide <c>transaction.Amount</c> by construction and
    /// <c>WeightedSplitRuleHandler.Among</c> drops departed members. An itemised rule divides
    /// its <em>receipt's</em> total and names whoever ate, so it is the first that can do
    /// both, and the next kind should not have to remember either.
    /// <para>
    /// A defect rather than a refusal when it is a rule's doing: the caller of a create or an
    /// edit did nothing wrong, and there is no field for them to correct. The stated path
    /// still refuses first, in <see cref="AsStated"/>, with the figures and the shortfall a
    /// dialog needs -- so this is reached by a person only when a rule produced the division,
    /// and then what it reports is which rule.
    /// </para>
    /// </remarks>
    private static void RefuseIfItIsNotAStorableDivision(
        Expense expense, IReadOnlyList<SplitAmount> splits, IReadOnlyCollection<Guid> members)
    {
        var total = splits.Sum(split => split.Amount);

        if (total != expense.Amount)
            throw new UnprocessableException(ErrorCodes.SplitsDoNotSumToAmount,
                    $"The shares add up to {total}, but the expense is {expense.Amount}.")
                .WithExtension("amount", expense.Amount)
                .WithExtension("splitTotal", total)
                .WithExtension("difference", expense.Amount - total);

        var strangers = splits
            .Where(split => !members.Contains(split.UserId))
            .Select(split => split.UserId)
            .ToList();

        if (strangers.Count > 0)
            throw new ConflictException(ErrorCodes.SplitUserNotInGroup,
                    "The division gives a share to somebody who is neither a member of the " +
                    "group nor invited to it.")
                // Named, because on this path nobody typed them: they are on the expense's
                // bill or in its rule, and which person it is is the whole of what a caller
                // needs to go and fix.
                .WithExtension("userIds", strangers);
    }

    /// <summary>
    /// The amounts the caller stated, checked and taken as they are.
    /// </summary>
    /// <remarks>
    /// Nothing is adjusted here. A set that does not sum to the amount is refused rather
    /// than balanced by moving the difference onto somebody, because which somebody is a
    /// decision the person making the split has already expressed an opinion about, and
    /// silently overruling it is how a share nobody agreed to gets stored.
    /// </remarks>
    private static IReadOnlyList<SplitAmount> AsStated(
        IReadOnlyList<SplitInput> given, decimal amount, IReadOnlyCollection<Guid> members)
    {
        if (given.Count == 0)
            throw new ValidationException(ErrorCodes.SplitsInvalid,
                "A split has to name somebody. Leave the splits out entirely to divide by the category.");

        if (given.Select(split => split.UserId).Distinct().Count() != given.Count)
            throw new ValidationException(ErrorCodes.SplitsInvalid,
                "A member may appear in a split only once.");

        if (given.Any(split => !members.Contains(split.UserId)))
            throw new ConflictException(ErrorCodes.SplitUserNotInGroup,
                "A split names somebody who is neither a member of the group nor invited to it.");

        var total = given.Sum(split => split.Amount);

        if (total != amount)
            throw new UnprocessableException(ErrorCodes.SplitsDoNotSumToAmount,
                    $"The shares add up to {total}, but the expense is {amount}.")
                // The shortfall, so a dialog can say "8.00 left to assign" rather than
                // making the person add the column up themselves.
                .WithExtension("amount", amount)
                .WithExtension("splitTotal", total)
                .WithExtension("difference", amount - total);

        return [.. given.Select(split => new SplitAmount(split.UserId, split.Amount))];
    }

    /// <summary>
    /// Everybody this expense may be divided between.
    /// </summary>
    /// <remarks>
    /// The group's participants and not only its members: somebody invited and still to
    /// answer is choosable here, because spending does not wait for people to answer their
    /// invitations. Their share is an ordinary share -- it is stored the same way, counted
    /// in the same balances, and belongs to them the moment they accept.
    /// </remarks>
    private async Task<IReadOnlyCollection<Guid>> MembersOf(Expense expense, CancellationToken ct)
    {
        var groupId = expense.Group?.Id ?? expense.GroupId;

        if (groupId is null)
            return [expense.Payer];

        return await participants.IdsOf(groupId.Value, ct);
    }

    /// <summary>
    /// The version to divide by: the one this expense was already written under when that
    /// is still a version of the rule its category names, and otherwise whatever that rule
    /// says now. Null when there is no category or it names no rule.
    /// </summary>
    /// <remarks>
    /// The whole of "divide it again by the rule it had at the time". An expense whose
    /// amount or payer is edited is re-divided by the version it was created under, even
    /// when the rule has been edited twice since -- because what changed is the expense, and
    /// nobody editing an amount is asking to be re-billed under a rule agreed later.
    /// <para>
    /// Moving it to another category is the case where that reasoning runs out: the version
    /// it holds belongs to a rule this expense is no longer filed under, so it is divided by
    /// the new category's rule as it stands, exactly as a fresh expense there would be. The
    /// test is which rule the version belongs to and not whether it is the current one,
    /// which is what keeps an edit from silently re-billing under a newer version.
    /// </para>
    /// </remarks>
    private async Task<SplitRuleVersion?> VersionFor(Expense expense, CancellationToken ct)
    {
        var categoryId = expense.Category?.Id ?? expense.CategoryId;

        if (categoryId is null)
            return null;

        var ruleId = await dbContext.Set<Category>()
            .Where(category => category.Id == categoryId)
            .Select(category => category.DefaultSplitRuleId)
            .FirstOrDefaultAsync(ct);

        if (ruleId is null)
            return null;

        // The one it already holds, when that is still a version of this category's rule.
        if (expense.SplitRuleVersionId is { } writtenUnder)
        {
            var kept = await Versions()
                .FirstOrDefaultAsync(version =>
                    version.Id == writtenUnder && version.SplitRuleId == ruleId, ct);

            if (kept is not null)
                return kept;
        }

        return await Versions()
            .FirstOrDefaultAsync(version =>
                version.SplitRuleId == ruleId && version.SupersededAt == null, ct);
    }

    /// <summary>
    /// Versions loaded the way a division needs them: the participants it weighs by, and
    /// the rule whose name a refusal has to quote.
    /// </summary>
    /// <remarks>
    /// Read as versions in their own right rather than reached through the category, because
    /// an Include cannot follow a Select that changed what the query is about.
    /// </remarks>
    private IQueryable<SplitRuleVersion> Versions() =>
        dbContext.Set<SplitRuleVersion>()
            .Include(version => (version as WeightedSplitRuleVersion)!.Participants)
            .Include(version => version.SplitRule);

    /// <summary>
    /// Swaps the stored splits for the ones just worked out.
    /// </summary>
    /// <remarks>
    /// Whether the expense is already tracked decides how a new split has to be attached,
    /// and getting it wrong is silent until it is a lost row: ids here are
    /// client-generated, so EF sees a fresh split hanging off a tracked parent, finds a key
    /// already set, and concludes it must be an existing row to UPDATE rather than a new
    /// one to INSERT. On a create the expense is still detached and the cascade from Add
    /// does the right thing on its own.
    /// </remarks>
    private void Replace(
        Expense expense, IReadOnlyList<SplitAmount> splits, IReadOnlyCollection<Guid> members)
    {
        RefuseIfItIsNotAStorableDivision(expense, splits, members);

        var expenseIsTracked = dbContext.Entry(expense).State is not EntityState.Detached;

        // Materialised first: marking a child deleted makes EF take it out of this very
        // collection, and a collection cannot be enumerated while it is being emptied.
        var superseded = expense.Splits.ToList();

        if (superseded.Count > 0)
        {
            // Only a tracked expense's shares are the context's to delete. A detached one is
            // a draft -- the update preview builds one carrying the stored division, so
            // IsWhatItAlreadyHolds has something to compare against -- and its shares are
            // copies that were never read from the table. Handing those to Remove would attach
            // deletions of rows the context has never seen, and the next save on the scope
            // would try to carry them out.
            if (expenseIsTracked)
                dbContext.RemoveRange(superseded);

            foreach (var split in superseded)
                expense.Splits.Remove(split);
        }

        foreach (var split in splits)
        {
            var row = new TransactionSplit
            {
                TransactionId = expense.Id,
                UserId = split.UserId,
                Amount = split.Amount
            };

            expense.Splits.Add(row);

            if (expenseIsTracked)
                dbContext.Entry(row).State = EntityState.Added;
        }
    }
}

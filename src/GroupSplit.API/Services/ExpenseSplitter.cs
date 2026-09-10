using GroupSplit.API.Errors;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
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

        var payerId = expense.User?.Id ?? expense.UserId;

        // A personal expense has nobody to divide with, so there is nothing a stated
        // division could say that is not either "all of it, to me" -- which is what it gets
        // anyway -- or a share for somebody who cannot see the expense at all. Refused by
        // name rather than left to fall out of the membership check below, which would
        // report a group the expense does not have.
        if (given is not null && (expense.Group?.Id ?? expense.GroupId) is null)
            throw new ValidationException(ErrorCodes.SplitOnAPersonalExpense,
                "A personal expense is not shared with anybody, so it cannot be split.");

        var members = await MembersOf(expense, ct);

        var splits = given is null
            ? await DividedByRule(expense, payerId, members, ct)
            : AsStated(given, expense.Amount, members);

        Replace(expense, splits);
    }

    /// <summary>
    /// The division the expense's category calls for, or an even one when it has no
    /// category or the category names no rule.
    /// </summary>
    private async Task<IReadOnlyList<SplitAmount>> DividedByRule(
        Expense expense, Guid payerId, IReadOnlyCollection<Guid> members, CancellationToken ct)
    {
        var rule = await DefaultRuleFor(expense, ct);

        // No category, or a category that names no rule: evenly between the members. That
        // is the whole of what a group with no rules used to be unable to do.
        if (rule is null)
            return SplitCalculator.DivideEvenly(expense.Amount, payerId, members);

        try
        {
            return splitRules.Divide(rule, expense.Amount, payerId, members);
        }
        catch (ArgumentException reason)
        {
            // A rule can be left with nothing to divide by. Everybody it named has gone --
            // a member who left, or somebody invited whose invitation was declined or
            // withdrawn -- and both take the name out of the rule rather than zeroing it,
            // so a shares rule that named one person keeps none. SplitCalculator says so by
            // throwing, which is right of it: no participants, or weights summing to zero,
            // is not a division it could carry out.
            //
            // What was wrong was where that surfaced. An ArgumentException is nobody's
            // domain error, so it reached the client as a 500 with a trace id, and the
            // person it happened to was somebody recording a dinner. The rule really is
            // unusable and saying which one is the useful half of the answer.
            throw new ValidationException(ErrorCodes.SplitRuleInvalid,
                    $"\"{rule.Name}\" no longer divides between anybody, so an expense filed " +
                    "under this category cannot be split by it. Edit the rule, or file the " +
                    "expense under nothing to divide it evenly.")
                .WithExtension("splitRuleId", rule.Id)
                .WithExtension("splitRuleName", rule.Name)
                .WithExtension("reason", reason.Message);
        }
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
            return [expense.User?.Id ?? expense.UserId];

        return await participants.IdsOf(groupId.Value, ct);
    }

    /// <summary>
    /// The rule the expense's category points at, with its participants, or null when
    /// there is no category or it names no rule.
    /// </summary>
    private async Task<SplitRule?> DefaultRuleFor(Expense expense, CancellationToken ct)
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

        // Loaded as a rule in its own right rather than reached through the category: an
        // Include cannot follow a Select that changed what the query is about.
        return await dbContext.Set<SplitRule>()
            .Include(rule => (rule as WeightedSplitRule)!.Participants)
            .FirstOrDefaultAsync(rule => rule.Id == ruleId, ct);
    }

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
    private void Replace(Expense expense, IReadOnlyList<SplitAmount> splits)
    {
        // Materialised first: marking a child deleted makes EF take it out of this very
        // collection, and a collection cannot be enumerated while it is being emptied.
        var superseded = expense.Splits.ToList();

        if (superseded.Count > 0)
        {
            dbContext.RemoveRange(superseded);

            foreach (var split in superseded)
                expense.Splits.Remove(split);
        }

        var expenseIsTracked = dbContext.Entry(expense).State is not EntityState.Detached;

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

using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IExpenseSplitter
{
    Task WriteSplitsAsync(Expense expense, CancellationToken ct = default);
}

/// <summary>
/// Works out what each person owed on an expense, and stores it.
/// </summary>
/// <remarks>
/// The one place a split is decided. It runs on create and on every edit, because the
/// amount, the payer and the category each change what everybody owed -- and a stored split
/// that no longer sums to its amount makes every balance in the group wrong with nothing
/// else to catch it.
/// </remarks>
public class ExpenseSplitter(AppDbContext dbContext, ISplitRuleHandler splitRules) : IExpenseSplitter
{
    public async Task WriteSplitsAsync(Expense expense, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        var payerId = expense.User?.Id ?? expense.UserId;

        var members = await MembersOf(expense, ct);

        var rule = await DefaultRuleFor(expense, ct);

        // No category, or a category that names no rule: evenly between the members. That
        // is the whole of what a group with no rules used to be unable to do.
        var splits = rule is null
            ? SplitCalculator.DivideEvenly(expense.Amount, payerId, members)
            : splitRules.Divide(rule, expense.Amount, payerId, members);

        Replace(expense, splits);
    }

    private async Task<IReadOnlyCollection<Guid>> MembersOf(Expense expense, CancellationToken ct)
    {
        var groupId = expense.Group?.Id ?? expense.GroupId;

        if (groupId is null)
            return [expense.User?.Id ?? expense.UserId];

        return await dbContext.Set<Group>()
            .Where(@group => @group.Id == groupId)
            .SelectMany(@group => @group.Users)
            .Select(user => user.Id)
            .ToListAsync(ct);
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

        return await dbContext.Set<Category>()
            .Where(category => category.Id == categoryId)
            .Select(category => category.DefaultSplitRule)
            .Include(rule => (rule as WeightedSplitRule)!.Participants)
            .FirstOrDefaultAsync(ct);
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

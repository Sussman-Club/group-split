using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data.Splitting;

/// <summary>
/// Turning an expense's rule into the amounts each person owed, and storing them on it.
/// </summary>
/// <remarks>
/// Here rather than in the API because the seeder needs exactly the same answer. Seed data
/// that divided its expenses even slightly differently from the way the app does would
/// give every developer a set of balances that no sequence of user actions could produce.
/// </remarks>
public static class ExpenseSplitting
{
    /// <summary>
    /// Divides <paramref name="expense"/> by its rule version and replaces whatever splits
    /// it was carrying.
    /// </summary>
    /// <remarks>
    /// Called on create and on every edit: the amount, the payer and the rule each change
    /// what everybody owed, and a stored split that no longer sums to its amount makes
    /// every balance in the group wrong with nothing else to catch it.
    /// </remarks>
    public static async Task WriteSplitsAsync(this AppDbContext context, Expense expense,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expense);

        var payerId = expense.User?.Id ?? expense.UserId;

        var weights = await context.WeightsForAsync(expense.RuleVersion, cancellationToken);

        var splits = weights.Count > 0
            ? SplitCalculator.Divide(expense.Amount, payerId, weights)
            // A rule that names nobody -- the personal default -- is the payer's alone.
            : [new SplitAmount(payerId, expense.Amount)];

        // Materialised first: marking a child deleted makes EF take it out of this very
        // collection, and a collection cannot be enumerated while it is being emptied.
        var superseded = expense.Splits.ToList();

        if (superseded.Count > 0)
        {
            context.RemoveRange(superseded);

            foreach (var split in superseded)
                expense.Splits.Remove(split);
        }

        // Whether the expense is already tracked decides how a new split has to be
        // attached, and getting it wrong is silent until it is a lost row: ids here are
        // client-generated, so EF sees a fresh split hanging off a tracked parent, finds a
        // key already set, and concludes it must be an existing row to UPDATE rather than
        // a new one to INSERT. On a create the expense is still detached and the cascade
        // from Add does the right thing on its own.
        var expenseIsTracked = context.Entry(expense).State is not EntityState.Detached;

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
                context.Entry(row).State = EntityState.Added;
        }
    }

    /// <summary>
    /// A rule version read as weights, which is all the arithmetic wants from it.
    /// </summary>
    /// <remarks>
    /// Percentages are stored as a double in percent -- 33.33 -- and become hundredths of
    /// a percent, so a weight is a whole number and the division has nothing to round
    /// twice. A version that is not a percentage rule has no participants at all, which
    /// the caller reads as "the payer's alone".
    /// </remarks>
    private static async Task<IReadOnlyList<SplitWeight>> WeightsForAsync(this AppDbContext context,
        RuleVersion version, CancellationToken cancellationToken)
    {
        if (version is not PercentRuleVersion percentRuleVersion)
            return [];

        var participants = await context.Entry(percentRuleVersion)
            .Collection(rule => rule.RuleUsers)
            .Query()
            .Select(ruleUser => new { ruleUser.UserId, ruleUser.Percentage })
            .ToListAsync(cancellationToken);

        return
        [
            ..participants.Select(participant =>
                new SplitWeight(participant.UserId, (int)Math.Round(participant.Percentage * 100)))
        ];
    }
}

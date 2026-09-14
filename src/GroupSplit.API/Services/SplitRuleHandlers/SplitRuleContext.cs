using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>The amount and expense context needed to divide either an expense or one item.</summary>
public sealed record SplitRuleContext(decimal Amount, Guid Payer, Guid? GroupId = null, Receipt? Receipt = null)
{
    /// <summary>
    /// The navigation first, because an expense is divided before it is added: the FK is
    /// still null until EF fixes it up, and reading it alone made every itemized expense
    /// look like it belonged to no group -- which is what an item's rule can never match.
    /// </summary>
    public static implicit operator SplitRuleContext(Transaction transaction) =>
        new(transaction.Amount, transaction.Payer, transaction.Group?.Id ?? transaction.GroupId,
            (transaction as Expense)?.Receipt);
}

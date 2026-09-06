namespace GroupSplit.Data.Entities;

/// <summary>
/// Something the group spent. The only kind of transaction a category makes sense on, and
/// the only kind that belongs in an expense list or a total.
/// </summary>
/// <remarks>
/// Every surface that means *expenses* reads <c>Set&lt;Expense&gt;()</c> and gets EF's
/// discriminator predicate for free, which is why settlements stopped needing to be
/// filtered out of the listings and the totals: they are no longer in them.
/// </remarks>
public class Expense : Transaction
{
    /// <summary>
    /// The rule version this was divided by.
    /// </summary>
    /// <remarks>
    /// Transitional, and on the leaf rather than the base because a transfer never had
    /// one. The splits are now the record of what each person owed, so this survives only
    /// to say which category the expense was filed under until <see cref="Category"/>
    /// takes that over.
    /// </remarks>
    public virtual RuleVersion RuleVersion { get; set; } = null!;

    internal Guid RuleVersionId { get; set; }
}

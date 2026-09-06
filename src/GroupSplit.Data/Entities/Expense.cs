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
    /// What it was for, or null for an expense filed under nothing.
    /// </summary>
    /// <remarks>
    /// Nullable, and that is the point of the change. The rule this replaces was the
    /// category and the split at once, so an expense had to name one that carried a
    /// division or it could not be recorded at all -- which is why a group with no rules
    /// had four error codes explaining what it could not do. An expense with no category
    /// divides evenly and is perfectly ordinary.
    /// <para>
    /// It says what the expense was filed under, not how it was divided. How it was divided
    /// is <see cref="Transaction.Splits"/>, decided when it was written, so re-pointing a
    /// category at a different rule does not restate what anybody owed last March.
    /// </para>
    /// </remarks>
    public virtual Category? Category { get; set; }

    public Guid? CategoryId { get; set; }
}

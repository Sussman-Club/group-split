using System.ComponentModel.DataAnnotations.Schema;

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

    /// <summary>
    /// The lines of a bill that are this expense's money. Empty for the ordinary expense
    /// nobody itemised.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="ReceiptItem.Expense"/>, and the way round it is because one
    /// bill can be more than one expense: a warehouse charge covering the flat's groceries
    /// and a jacket of your own is one receipt whose lines name two expenses. An expense
    /// therefore holds lines rather than a receipt -- and reaches the bill, when it needs the
    /// tax and the tip, through any one of them.
    /// <para>
    /// Empty is not a broken row. An expense filed under an itemised rule with no lines is
    /// one nobody has itemised yet, and the division says so by name rather than the model
    /// forbidding it.
    /// </para>
    /// </remarks>
    public virtual ICollection<ReceiptItem> ReceiptItems { get; } = [];

    /// <summary>
    /// The whole bill this expense is one part of, put here by whoever is about to divide it.
    /// Never stored.
    /// </summary>
    /// <remarks>
    /// A division by items needs more of the paper than the expense's own lines: the tax and
    /// the tip are stated once for the whole bill, and how much of them is this part's
    /// depends on what the other parts hold. <see cref="ReceiptItems"/> cannot answer that,
    /// so the splitter loads the receipt and hands it over here.
    /// <para>
    /// Set explicitly rather than left to EF's navigation fixup, which would fill
    /// <see cref="ReceiptItems"/> for a tracked expense and not for a detached one -- and the
    /// update preview divides a detached draft. A division that worked on the save and failed
    /// on the preview of the same edit is the bug this avoids.
    /// </para>
    /// </remarks>
    [NotMapped]
    public Receipt? Bill { get; set; }
}

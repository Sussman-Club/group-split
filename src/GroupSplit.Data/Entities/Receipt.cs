namespace GroupSplit.Data.Entities;

/// <summary>
/// The bill behind a card charge, itemised: what was bought, at what price, and what the tax
/// and the tip came to on top.
/// </summary>
/// <remarks>
/// What earns a table of its own is <see cref="Items"/> and, through them,
/// <see cref="ReceiptItemClaim"/>: "Omar had the beer" is a fact about one bill on one
/// night, and it is the only way to divide a shared dinner where nobody ate the same thing.
/// <para>
/// A bill is not an expense, and that is the distinction the shape now turns on. One trip to
/// a warehouse shop is a single charge covering the flat's groceries and a jacket that is
/// nobody's business but yours -- two purchases on one piece of paper. So a receipt is not
/// attached to an expense; its <em>lines</em> are, through
/// <see cref="ReceiptItem.ExpenseId"/>, and each distinct expense they name is one part of
/// the bill. A restaurant bill is the same shape with one part, which is why nothing about
/// dividing a dinner changed when this arrived.
/// </para>
/// <para>
/// <see cref="Tax"/> and <see cref="Tip"/> are stored apart from <see cref="Subtotal"/>
/// rather than folded into the items, because apportioning them is the whole difficulty.
/// They are apportioned twice now, at two scales, by the same arithmetic: between the parts
/// in proportion to the lines each holds, and then inside a part between the people who
/// claimed those lines. <c>ReceiptSplitCalculator</c> is where both live.
/// </para>
/// <para>
/// Three invariants, enforced by <c>ReceiptService</c> rather than by the column types, since
/// none is expressible as one: <see cref="Subtotal"/> + <see cref="Tax"/> + <see cref="Tip"/>
/// equals <see cref="Total"/>; the parts' amounts sum to <see cref="Total"/>; and each part's
/// amount is its own expense's <see cref="Transaction.Amount"/>. The last is the load-bearing
/// one -- the shares a part produces are stored as its expense's splits, and those must sum
/// to that expense's amount or every balance in the group is wrong.
/// <c>ExpenseSplitter.Replace</c> refuses the division outright if it ever fails to.
/// </para>
/// <para>
/// No merchant here, deliberately. <see cref="Transaction.Merchant"/> already says where the
/// money went, and a second pointer at the same fact is a second pointer to keep in step.
/// </para>
/// <para>
/// No photograph either, for now. The lines are what divides a bill and the picture would
/// only be evidence of them, so storing a URL would mean hosting images before anything
/// reads one -- and a column nothing writes is worse than an absent feature, because it
/// looks like one that works.
/// </para>
/// </remarks>
public class Receipt : Entity
{
    /// <summary>
    /// The imported bank row this bill was typed against, or null for one typed straight onto
    /// an expense with no bank behind it.
    /// </summary>
    /// <remarks>
    /// What lets a bill be itemised before it is an expense at all: the card is charged at the
    /// shop, the row lands in the inbox, and the lines and the claims can be entered at the
    /// table while everybody still remembers who had what. Splitting the row then gives each
    /// line an expense.
    /// <para>
    /// Kept afterwards rather than cleared. The receipt no longer belongs to an expense, so
    /// there is no hand-over to make and nothing for a second link to contradict -- and the
    /// row is the one thing that goes on being true of the whole bill however many expenses
    /// it is split into.
    /// </para>
    /// <para>
    /// The only owner the database knows about, which is why it cascades: a bank row that is
    /// removed takes its bill with it. A receipt with no row is owned by the expenses its
    /// lines name, and <c>ReceiptService</c> removes it when the last of them goes -- a rule
    /// no check constraint could carry, since it spans two tables.
    /// </para>
    /// </remarks>
    public virtual BankTransaction? BankTransaction { get; set; }

    public Guid? BankTransactionId { get; set; }

    /// <summary>What the items came to, before tax and tip.</summary>
    public required decimal Subtotal { get; set; }

    /// <summary>Tax on the whole bill, apportioned between the parts and then within them.</summary>
    public decimal Tax { get; set; }

    /// <summary>Tip on the whole bill, apportioned the same way as <see cref="Tax"/>.</summary>
    public decimal Tip { get; set; }

    /// <summary>
    /// What was paid, and what the parts' amounts must sum to.
    /// </summary>
    /// <remarks>
    /// Stored rather than summed from the parts on every read, so the row can be checked
    /// against itself: a receipt whose parts stop adding up is a transcription error worth
    /// refusing, and there is nothing to compare against if the total is only ever derived.
    /// </remarks>
    public required decimal Total { get; set; }

    /// <summary>What was on the bill, line by line.</summary>
    public virtual ICollection<ReceiptItem> Items { get; } = [];
}

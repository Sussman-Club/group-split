namespace GroupSplit.Data.Entities;

/// <summary>
/// The bill behind a payment, itemised: what was bought, at what price, and what the tax and
/// the tip came to on top.
/// </summary>
/// <remarks>
/// What earns a table of its own is <see cref="Items"/> and, through them,
/// <see cref="ReceiptItemClaim"/>: "Omar had the beer" is a fact about one bill on one
/// night, and it is the only way to divide a shared dinner where nobody ate the same thing.
/// <para>
/// <see cref="Tax"/> and <see cref="Tip"/> are stored apart from <see cref="Subtotal"/>
/// rather than folded into the items, because apportioning them is the whole difficulty. A
/// person who claims a quarter of the food owes a quarter of the tax and a quarter of the
/// tip, and that cannot be worked out from item prices that already have an unknown share of
/// both baked in. <c>ReceiptSplitCalculator</c> is where that division lives.
/// </para>
/// <para>
/// Two invariants, enforced by <c>ReceiptService</c> rather than by the column types, since
/// neither is expressible as one: <see cref="Subtotal"/> + <see cref="Tax"/> +
/// <see cref="Tip"/> equals <see cref="Total"/>, and -- once there is an expense --
/// <see cref="Total"/> equals its <see cref="Transaction.Amount"/>. The second is the
/// load-bearing one: the shares a receipt produces are stored as the expense's splits, and
/// those must sum to the expense's amount or every balance in the group is wrong.
/// <c>ExpenseSplitter.Replace</c> refuses the division outright if it ever fails to, which is
/// the backstop for the paths this class does not own -- linking a bank row to an expense of
/// a different amount, say.
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
    /// The expense this is the bill for, or null for one typed up before anybody filed it.
    /// </summary>
    /// <remarks>
    /// An <see cref="Entities.Expense"/> and not a <see cref="Transaction"/>, because a
    /// transfer is one member paying another back and has no items to divide.
    /// <para>
    /// Nullable so that a bill can exist before the expense does -- see
    /// <see cref="BankTransaction"/>. Set when the row is filed, and what makes the division
    /// possible: a receipt with no expense has items and claims and nowhere to write the
    /// shares.
    /// </para>
    /// </remarks>
    public virtual Expense? Expense { get; set; }

    /// <summary>
    /// Public, like the other foreign keys a service reads directly: a receipt is addressed
    /// by what it belongs to far more often than by its own id.
    /// </summary>
    public Guid? ExpenseId { get; set; }

    /// <summary>
    /// The imported bank row this bill belongs to, when it started there. Null for one
    /// somebody attached to an expense they typed.
    /// </summary>
    /// <remarks>
    /// What lets a bill be itemised before it is an expense at all: the card is charged at
    /// the restaurant, the row lands in the inbox, and the lines and the claims can be
    /// entered at the table while everybody still remembers who had what.
    /// <para>
    /// Cleared when the row is filed, which is the same moment <see cref="Expense"/> is set:
    /// a receipt has exactly one owner, and filing hands it from the row to the expense. That
    /// is what the check constraint says and what lets both foreign keys be plain cascades --
    /// two owners would mean two cascade paths and a question about whether unlinking a bank
    /// destroys a filed bill. Nothing is lost, because where an expense came from is recorded
    /// on the expense, in <see cref="Transaction.BankTransactionId"/>.
    /// </para>
    /// </remarks>
    public virtual BankTransaction? BankTransaction { get; set; }

    public Guid? BankTransactionId { get; set; }

    /// <summary>What the items came to, before tax and tip.</summary>
    public required decimal Subtotal { get; set; }

    /// <summary>Tax on the whole bill, apportioned by what each person claimed.</summary>
    public decimal Tax { get; set; }

    /// <summary>Tip on the whole bill, apportioned the same way as <see cref="Tax"/>.</summary>
    public decimal Tip { get; set; }

    /// <summary>
    /// What was actually paid, and what the expense's amount must equal once there is one.
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

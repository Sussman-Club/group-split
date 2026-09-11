namespace GroupSplit.Data.Entities;

/// <summary>
/// One line on a bill: what it was, how many, and what it cost.
/// </summary>
/// <remarks>
/// <see cref="TotalPrice"/> is stored rather than multiplied out of
/// <see cref="UnitPrice"/> and <see cref="Quantity"/>, because the paper does not always
/// agree with the arithmetic -- a two-for-one, a line discount, a price rounded at the till
/// -- and the bill is the record. The division reads this one and nothing else, so a line
/// that does not multiply out is transcribed faithfully instead of being corrected into
/// something nobody was charged.
/// </remarks>
public class ReceiptItem : Entity
{
    public virtual Receipt Receipt { get; set; } = null!;

    /// <summary>
    /// Public, because which bill a line belongs to is the line's own data -- the service
    /// that divides a receipt reads it outside this assembly.
    /// </summary>
    public Guid ReceiptId { get; set; }

    public required string Name { get; set; }

    /// <summary>What one of them cost.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// How many. Decimal rather than an integer count, because bills are written in
    /// kilos and litres as readily as in units.
    /// </summary>
    public decimal Quantity { get; set; } = 1;

    /// <summary>
    /// What the line came to, and the only figure the division uses. See the remarks on the
    /// type for why it is not derived.
    /// </summary>
    public required decimal TotalPrice { get; set; }

    /// <summary>
    /// How this line is divided. Claimed by default, which is the one that needs
    /// <see cref="Claims"/> to say anything.
    /// </summary>
    /// <remarks>
    /// What makes a line behave like a small transaction of its own: it has an amount and a
    /// division, and the division need not be the same as the line above it. "These two are
    /// mine and the rest is shared" is two claimed lines and the others left
    /// <see cref="ReceiptItemDivision.Evenly"/>.
    /// <para>
    /// Defaulted to <see cref="ReceiptItemDivision.Claimed"/> so that the safe behaviour is
    /// the one you get by saying nothing: a line nobody has claimed stops the bill being
    /// divided rather than quietly landing on everybody.
    /// </para>
    /// </remarks>
    public ReceiptItemDivision Division { get; set; } = ReceiptItemDivision.Claimed;

    /// <summary>
    /// Who had it, and in what proportion. Read only when <see cref="Division"/> is
    /// <see cref="ReceiptItemDivision.Claimed"/>; empty then means nobody has claimed the
    /// line yet, which is an ordinary state while a bill is being worked through and a
    /// refusal once it comes to dividing it.
    /// </summary>
    public virtual ICollection<ReceiptItemClaim> Claims { get; } = [];
}

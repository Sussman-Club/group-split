namespace GroupSplit.Data.Entities;

/// <summary>
/// One person's part of one line on a bill -- "Omar had the beer", or "the wine, between the
/// three of us".
/// </summary>
/// <remarks>
/// A table rather than a nullable <c>UserId</c> on <see cref="ReceiptItem"/>, because a
/// shared line is the common case and not the exception: a bottle between three of five
/// diners cannot be said at all with one pointer, and splitting the line into three rows
/// would restate the bill into something the paper does not say.
/// <para>
/// This is deliberately <em>not</em> a <see cref="SplitRuleVersion"/>, and the distinction
/// is worth keeping straight. A rule version is a division the group has named and can point
/// a category at, and it carries its own division so that the row a transaction points at
/// says the same thing tomorrow as it said when the expense was written. Claims carry the
/// division for one bill, they are edited while people are still working out who had what,
/// and no second expense could ever be filed under them. So they produce an expense's shares
/// the way a person typing amounts does -- through <c>ExpenseSplitter</c>'s stated-splits
/// path, leaving <see cref="Transaction.SplitRuleVersion"/> null -- and the receipt itself is
/// the record of where those amounts came from.
/// </para>
/// </remarks>
public class ReceiptItemClaim : Entity
{
    public virtual ReceiptItem ReceiptItem { get; set; } = null!;

    public Guid ReceiptItemId { get; set; }

    public virtual User User { get; set; } = null!;

    /// <summary>
    /// Public, like <see cref="SplitRuleParticipant.UserId"/> and for the same reason: whose
    /// claim this is belongs to the claim rather than to how EF indexes it.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Their part of the line, in proportion to the other claims on it. One apiece -- the
    /// default -- is an even share between whoever claimed it.
    /// </summary>
    /// <remarks>
    /// A weight rather than an amount, so that a line's claims cannot disagree with the
    /// line's price. Amounts would let three people claim 4.00 each of a 10.00 bottle and
    /// leave 2.00 belonging to nobody, on a table whose whole purpose is to sum to the bill.
    /// <para>
    /// Unlike <see cref="SplitRuleParticipant.Weight"/>, zero is refused rather than stored.
    /// There it means a member the rule deliberately excludes, which is worth distinguishing
    /// from one the rule never named; here the line's claims <em>are</em> the list of people
    /// who had it, so somebody with no part of it simply has no row.
    /// </para>
    /// </remarks>
    public int Weight { get; set; } = 1;
}

namespace GroupSplit.Data.Entities;

/// <summary>
/// How one line of a bill is divided.
/// </summary>
/// <remarks>
/// A line is the smallest thing on a receipt that money can be attached to, and this is what
/// makes it behave like a little transaction: it has an amount, and it has a division, and
/// the division need not be the same as the line above it. What it deliberately does not
/// have is a <see cref="SplitRule"/> -- a rule is a division the group has named and can
/// point a category at, and "the paella" is not that.
/// <para>
/// Two kinds, and the boundary between them is the whole reason this type exists rather than
/// everything being claims. <see cref="ReceiptItemClaim"/> already says everything a list of
/// people with weights can say, which is every proportional division there is: one person,
/// several sharing evenly, or one of them having twice as much. Anything that can be pinned
/// to names is a claim and does not belong here -- "all of it on whoever paid" included,
/// since the payer is known by the time anything is divided.
/// </para>
/// <para>
/// <see cref="Evenly"/> is the one that cannot. Naming everybody says the same thing today
/// and a different thing the moment the roster moves, and the two are treated differently on
/// purpose: a claim by somebody who has left refuses the division, because there is no honest
/// way to redistribute a steak they ordered, while a line that was never anybody's in
/// particular simply divides between whoever is there now.
/// </para>
/// </remarks>
public enum ReceiptItemDivision
{
    /// <summary>
    /// Between the people who claimed it, in proportion to their weights. The default, and
    /// the one that needs the line to be claimed: a line left claimed-by-nobody is what stops
    /// a bill being divided, deliberately.
    /// </summary>
    Claimed = 0,

    /// <summary>
    /// Evenly between everybody the expense could be divided between, naming none of them.
    /// </summary>
    /// <remarks>
    /// The answer to "these two are mine and the rest is shared", and the reason nobody has
    /// to type four ids against the eight lines of a bill where only the wine was one
    /// person's.
    /// </remarks>
    Evenly = 1
}

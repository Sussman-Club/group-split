namespace GroupSplit.Shared;

/// <summary>
/// An itemised bill as the client reads it back.
/// </summary>
/// <param name="ExpenseId">
/// The purchase this reading is about -- one part of the bill -- or null when the whole
/// paper is being read rather than any one part of it, which is what an unfiled bank row
/// gets. The lines carry their own, so a split charge can be read whole.
/// </param>
/// <param name="BankTransactionId">
/// The imported row this bill is waiting on, or null once it has been filed onto an expense.
/// Exactly one of this and <paramref name="ExpenseId"/> is ever set: filing hands the receipt
/// over. Where a filed expense came from is on the expense itself.
/// </param>
/// <param name="UnclaimedItemCount">
/// How many lines of this part still belong to nobody -- of the whole bill, when no part was
/// asked for. The figure a client needs to say "3 items left to claim" without walking the
/// list, and the one thing standing between a part and being dividable.
/// </param>
/// <param name="CanDivide">
/// Whether asking to divide by this bill would succeed: it adds up, every line is claimed,
/// and there is an expense to write the shares to. Saves a client guessing at the rules.
/// </param>
public sealed record ReceiptResponse(
    Guid Id,
    Guid? ExpenseId,
    Guid? BankTransactionId,
    decimal Subtotal,
    decimal Tax,
    decimal Tip,
    decimal Total,
    int UnclaimedItemCount,
    bool CanDivide,
    IReadOnlyList<ReceiptItemResponse> Items);

/// <summary>One line on a bill, with how it divides and who had it.</summary>
/// <param name="IsTaxable">
/// Whether the bill's tax was charged on this line. The tax is weighed over the lines it was
/// charged on; the tip, which is about the bill rather than the goods, over all of them.
/// </param>
/// <param name="ExpenseId">
/// Which purchase this line's money is part of, or null while nobody has said. Each distinct
/// expense across the lines is one part of the bill.
/// </param>
public sealed record ReceiptItemResponse(
    Guid Id,
    string Name,
    decimal UnitPrice,
    decimal Quantity,
    decimal TotalPrice,
    bool IsTaxable,
    Guid? ExpenseId,
    IReadOnlyList<ReceiptClaimResponse> Claims);

/// <summary>
/// One person's part of one line.
/// </summary>
/// <param name="Share">
/// What their part of this line comes to, before tax and tip -- the line's price times their
/// weight over the weights on it.
/// </param>
/// <remarks>
/// <paramref name="Share"/> is here so a client can show "4.00" beside a name without
/// redoing the arithmetic, and it is deliberately <em>not</em> what anybody owes: the tax and
/// the tip are still to come, and they are apportioned across the whole bill rather than
/// line by line. What somebody owes is their split on the expense, which is the figure the
/// ledger keeps.
/// </remarks>
public sealed record ReceiptClaimResponse(
    Guid UserId,
    int Weight,
    decimal Share);

/// <summary>
/// What dividing a bill by its items would come to, per person.
/// </summary>
/// <remarks>
/// The same shape a split preview has, and for the same reason: somebody about to hand a
/// division to five people should see it first. Nothing is stored by asking.
/// </remarks>
public sealed record ReceiptDivisionResponse(
    Guid ReceiptId,
    decimal Total,
    IReadOnlyList<ReceiptShareResponse> Shares);

/// <summary>
/// What one person owes on a bill, and the part of it that is tax and tip rather than food.
/// </summary>
/// <param name="ClaimedSubtotal">What their lines came to, before tax and tip.</param>
/// <param name="Amount">
/// What they owe in total: <paramref name="ClaimedSubtotal"/> plus their share of the tax and
/// the tip, with the rounding settled. This is the figure that becomes their split.
/// </param>
public sealed record ReceiptShareResponse(
    Guid UserId,
    decimal ClaimedSubtotal,
    decimal Amount);

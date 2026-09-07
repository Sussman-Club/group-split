namespace GroupSplit.Shared;

/// <summary>
/// One expense the caller owes a share of, and what their share of it is.
/// </summary>
/// <remarks>
/// The other half of <see cref="TransactionResponse"/>. That one answers "what have I
/// paid" -- rows where the caller is the payer -- and this one answers "what do I owe",
/// which is a row per <c>TransactionSplit</c> against them, whoever paid. They are
/// different questions with different answers and the app could only ask the first.
/// <para>
/// It carries the whole expense as well as the share because those are two numbers and
/// only one of them is the caller's: "Dinner, 90.00, your share 30.00" is the row, and a
/// listing that showed either figure alone would be describing something nobody asked
/// about.
/// </para>
/// </remarks>
public record ExpenseShareResponse : TransactionResponse
{
    /// <summary>
    /// What this person owed on it. Not <see cref="TransactionResponse.Amount"/> divided
    /// by anything -- it is the stored split, the same row every balance is summed from.
    /// </summary>
    public decimal Share { get; init; }

    /// <summary>
    /// Whether the caller is also the payer, which is the one row that is not a debt.
    /// </summary>
    /// <remarks>
    /// Derivable from <see cref="TransactionResponse.PaidByUserId"/> by a caller who knows
    /// their own id, and stated anyway, because it is the distinction the whole listing
    /// turns on: your share of an expense you paid for yourself is money you already have,
    /// and adding it to what you owe would count the same expense twice. See
    /// <see cref="ExpenseShareSummaryResponse.OwedToOthers"/>.
    /// </remarks>
    public bool PaidByYou { get; init; }
}

/// <summary>
/// What a share listing adds up to over the whole match, rather than the page in hand.
/// Read with the same filter as the listing, like
/// <see cref="TransactionSummaryResponse"/> beside the expense one.
/// </summary>
/// <param name="Count">How many expenses the caller has a share of.</param>
/// <param name="Total">
/// What those expenses came to in full, whoever paid -- the money that moved, not the
/// caller's part of it.
/// </param>
/// <param name="Share">
/// The caller's part of it: every share in the match added up, their own expenses
/// included.
/// </param>
/// <param name="OwedToOthers">
/// The part of <paramref name="Share"/> that sits on an expense somebody else paid, which
/// is the only part of it that is a debt.
/// </param>
/// <remarks>
/// Four figures rather than one, because the obvious single figure is wrong in a way that
/// is hard to see. A person's share of an expense they paid for themselves is not
/// something they owe -- they are owed the rest of it -- so a total that summed every
/// share and called it "you owe" would count every personal expense and every dinner they
/// picked up as a debt to themselves. <paramref name="OwedToOthers"/> is the figure that
/// belongs beside the home page's position; <paramref name="Share"/> is the figure that
/// belongs beside a listing that shows those rows too.
/// <para>
/// It is still gross rather than net: settlements are transfers and are not expenses, so
/// nothing here has been paid back yet. <c>GET /users/me/position</c> remains the one
/// answer to "where do I stand".
/// </para>
/// </remarks>
public record ExpenseShareSummaryResponse(int Count, decimal Total, decimal Share, decimal OwedToOthers);

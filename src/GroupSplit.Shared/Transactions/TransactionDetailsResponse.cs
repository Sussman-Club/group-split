namespace GroupSplit.Shared;

/// <summary>
/// One person's share of an expense, as it is stored.
/// </summary>
/// <param name="UserId">
/// Who owes it. Carried beside the name because the edit dialog sends these back as
/// <see cref="SplitInput"/>, and a name is not something the API can be addressed by.
/// </param>
/// <param name="IsPendingInvitee">
/// True when the share belongs to somebody the group has invited and is still waiting on.
/// The share itself is ordinary -- it counts in the balances like any other -- but who
/// holds it is worth showing, because nobody should read the name as a member who joined.
/// </param>
public record TransactionSplitResponse(
    Guid UserId,
    string UserName,
    decimal Amount,
    bool IsPendingInvitee = false);

public record TransactionDetailsResponse : TransactionResponse
{
    public List<TransactionSplitResponse> Splits { get; init; } = [];

    /// <summary>
    /// The version of the rule that divided it, or null when no rule did -- shares somebody
    /// typed, or an even division under no rule at all.
    /// </summary>
    /// <remarks>
    /// Carried so a client can tell where the shares above came from, which the amounts
    /// alone do not say. The edit dialog needs it to open its split control on the truth:
    /// without it every expense read as automatically divided, and "Divided by the Rent
    /// rule" sat over amounts somebody had typed by hand.
    /// <para>
    /// Null is two answers rather than one, and the model does not separate them: an even
    /// division records no version either. A client that has to choose treats null as
    /// "these amounts are the expense's own", which is true of both -- the automatic
    /// division is reachable from there by asking for it.
    /// </para>
    /// </remarks>
    public Guid? SplitRuleVersionId { get; init; }
}

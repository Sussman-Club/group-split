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
}

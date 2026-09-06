namespace GroupSplit.Shared;

/// <summary>
/// One person's share of an expense, as it is stored.
/// </summary>
/// <param name="UserId">
/// Who owes it. Carried beside the name because the edit dialog sends these back as
/// <see cref="SplitInput"/>, and a name is not something the API can be addressed by.
/// </param>
public record TransactionSplitResponse(Guid UserId, string UserName, decimal Amount);

public record TransactionDetailsResponse : TransactionResponse
{
    public List<TransactionSplitResponse> Splits { get; init; } = [];
}

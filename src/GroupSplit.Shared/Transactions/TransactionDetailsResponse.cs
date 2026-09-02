namespace GroupSplit.Shared;

public record TransactionSplitResponse(string UserName, decimal Amount);

public record TransactionDetailsResponse : TransactionResponse
{
    public List<TransactionSplitResponse> Splits { get; init; } = [];
}

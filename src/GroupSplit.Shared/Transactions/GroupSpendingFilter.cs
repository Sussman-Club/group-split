namespace GroupSplit.Shared;

/// <summary>The filters shared by one group's spending total and its category breakdown.</summary>
public record GroupSpendingFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? PaidBy = null,
    Guid? CategoryId = null,
    bool? Uncategorized = null,
    string? Search = null,
    string? Category = null)
{
    public TransactionFilter ToTransactionFilter() => new(From, To, PaidByUserId: PaidBy, Category: Category, Search: Search);
}

using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>
/// Everything that decides which expenses are on screen: which page of them, in what
/// order, narrowed by what. One value rather than four fields, so the state can tell
/// "this is the page I am already holding" from "this is a different question" by
/// comparing it -- which is what keeps the grid from asking the server for what it has.
/// </summary>
public sealed record TransactionQuery(
    int Page = 1,
    int PageSize = PageRequest.DefaultPageSize,
    string SortBy = TransactionQuery.DefaultSortBy,
    bool SortDescending = true,
    string? Search = null)
{
    public const string DefaultSortBy = "dateTime";

    /// <summary>Newest first, at the default size: what the page opens on.</summary>
    public static readonly TransactionQuery Default = new();
}

using GroupSplit.App.Shared.Models;
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
    string? Search = null,
    DateFilter? Range = null,
    bool? Personal = null)
{
    /// <summary>
    /// Whose ledger: null for both, true for the expenses in no group at all, false for the
    /// ones in a group. Personal is the absence of a group rather than a group of its own,
    /// so it is a filter and not an id -- and asking for it is how somebody sees the
    /// expenses that used to be filed into a hidden group named "Personal".
    /// </summary>
    public bool? Personal { get; init; } = Personal;

    /// <summary>The span this asks for, or every day there is.</summary>
    public DateFilter EffectiveRange => Range ?? DateFilter.AllTime;

    public const string DefaultSortBy = "dateTime";

    /// <summary>Newest first, at the default size: what the page opens on.</summary>
    public static readonly TransactionQuery Default = new();
}

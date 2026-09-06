using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>
/// The state behind the expenses page. The operations return whether they completed: a
/// failure has already been shown to the person by the time they return false.
/// </summary>
public interface ITransactionsPageStateService
{
    /// <summary>The page on screen, or null before the first one has landed.</summary>
    PagedResponse<TransactionResponse>? Page { get; }

    /// <summary>The question <see cref="Page"/> is the answer to.</summary>
    TransactionQuery Query { get; }

    /// <summary>
    /// Everything the person has recorded, counted and totalled by the server. The tiles
    /// read these rather than adding up <see cref="Page"/>, which holds one page of it.
    /// </summary>
    TransactionSummaryResponse? Summary { get; }

    /// <summary>The same, for the calendar month in progress.</summary>
    TransactionSummaryResponse? MonthSummary { get; }

    /// <summary>
    /// The same again for what the current search matches, or null when nothing is being
    /// searched for.
    /// </summary>
    TransactionSummaryResponse? MatchesSummary { get; }

    event Action? OnTransactionsChanged;
    Task IsReadyTask { get; }

    /// <summary>
    /// Reads the page <paramref name="query"/> asks for. A query equal to the one already
    /// answered is served from what is held, so a component asking for what it is showing
    /// costs nothing.
    /// </summary>
    Task LoadAsync(TransactionQuery query, CancellationToken cancellationToken = default);

    Task<bool> CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(TransactionResponse transaction, JsonPatchDocument<UpdateTransactionRequest> patch,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(TransactionResponse transaction, CancellationToken cancellationToken = default);
}

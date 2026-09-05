using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>
/// The state behind the expenses page. The operations return whether they completed: a
/// failure has already been shown to the person by the time they return false.
/// </summary>
public interface ITransactionsPageStateService
{
    ICollection<TransactionResponse>? Transactions { get; }
    event Action? OnTransactionsChanged;
    Task IsReadyTask { get; }
    Task<bool> CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(TransactionResponse transaction, JsonPatchDocument<UpdateTransactionRequest> patch,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(TransactionResponse transaction, CancellationToken cancellationToken = default);
}

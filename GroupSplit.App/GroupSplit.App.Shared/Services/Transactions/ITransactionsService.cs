using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Transactions;

public interface ITransactionsService
{
    ICollection<TransactionResponse>? Transactions { get; }
    event Action? OnTransactionsChanged;
    Task IsReadyTask { get; }
    Task CreateAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(TransactionResponse transaction, JsonPatchDocument<UpdateTransactionRequest> patch,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(TransactionResponse transaction, CancellationToken cancellationToken = default);
}
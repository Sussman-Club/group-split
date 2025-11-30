using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Transactions;

public interface ITransactionsService
{
    ICollection<TransactionResponse>? UserTransactions { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task AddAsync(CreateTransactionRequest request);
    Task EditAsync(Guid id, JsonPatchDocument<UpdateTransactionRequest> patch);
    Task DeleteAsync(TransactionResponse transaction);
    Task<CreateTransactionRequest?> ShowCreateTransactionDialogAsync();
    Task<JsonPatchDocument<UpdateTransactionRequest>?> ShowEditTransactionDialogAsync(TransactionResponse transaction);
    Task<bool> ShowConfirmDeleteDialogAsync(TransactionResponse transaction);
}
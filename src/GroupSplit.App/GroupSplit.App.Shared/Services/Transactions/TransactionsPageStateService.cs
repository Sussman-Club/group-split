using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Transactions;

public class TransactionsPageStateService : ITransactionsPageStateService
{
    private readonly ITransactionsClient _client;
    private readonly TransactionsTracker _tracker;
    private readonly ISnackbar _snackbar;
    private readonly LoadGuard _guard;

    public Task IsReadyTask { get; }

    public TransactionsPageStateService(ITransactionsClient client,
        TransactionsTracker tracker,
        ISnackbar snackbar,
        LoadGuard guard)
    {
        _client = client;
        _tracker = tracker;
        _snackbar = snackbar;
        _guard = guard;
        IsReadyTask = Task.Run(async () =>
        {
            if (tracker.Transactions is not null) return;
            await _guard.RunAsync(() => LoadAsync(), "your expenses");
        });
    }

    public ICollection<TransactionResponse> Transactions
    {
        get => _tracker.Transactions ?? [];
        private set
        {
            _tracker.Transactions = value;
            OnTransactionsChanged?.Invoke();
        }
    }

    public event Action? OnTransactionsChanged;

    private async Task LoadAsync(CancellationToken ct = default)
    {
        Transactions = await _client.GetTransactionsAsAsyncEnumerable(cancellationToken: ct)
            .ToListAsync(cancellationToken: ct);
    }

    public async Task CreateAsync(CreateTransactionRequest request, CancellationToken ct = default)
    {
        var transaction = await _client.CreateTransactionAsync(request, ct);
        AddTransaction(transaction);
        _snackbar.Add("Transaction created successfully.", Severity.Success);
        await LoadAsync(ct);
    }

    public async Task UpdateAsync(TransactionResponse transaction, JsonPatchDocument<UpdateTransactionRequest> patch,
        CancellationToken ct = default)
    {
        var updated = await _client.UpdateTransactionAsync(transaction.Id, patch, ct);

        if (updated.PaidByUserId != transaction.PaidByUserId)
        {
            RemoveTransaction(transaction);
        }
        else
        {
            UpdateTransaction(updated);
        }
        
        _snackbar.Add("Transaction updated successfully.", Severity.Success);
    }

    public async Task DeleteAsync(TransactionResponse transaction, CancellationToken ct = default)
    {
        await _client.DeleteTransactionAsync(transaction.Id, ct);
        RemoveTransaction(transaction);
        _snackbar.Add("Transaction deleted successfully.", Severity.Success);
    }

    private void UpdateTransaction(TransactionResponse transaction)
    {
        var transactions = Transactions as List<TransactionResponse> ?? Transactions.ToList();
        var index = transactions.FindIndex(g => g.Id == transaction.Id);

        if (index < 0) return;

        transactions[index] = transaction;
        Transactions = transactions;
    }

    private void AddTransaction(TransactionResponse transaction)
    {
        var transactions = Transactions as List<TransactionResponse> ?? Transactions.ToList();
        transactions.Add(transaction);
        Transactions = transactions;
    }

    private void RemoveTransaction(TransactionResponse transaction)
    {
        var transactions = Transactions as List<TransactionResponse> ?? Transactions.ToList();
        transactions.Remove(transaction);
        Transactions = transactions;
    }
}
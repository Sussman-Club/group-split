using GroupSplit.App.Shared.Services.Errors;
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
    private readonly ApiErrorPresenter _errors;
    private readonly DataChangeNotifier _changes;

    public Task IsReadyTask { get; }

    public TransactionsPageStateService(ITransactionsClient client,
        TransactionsTracker tracker,
        ISnackbar snackbar,
        LoadGuard guard,
        ApiErrorPresenter errors,
        DataChangeNotifier changes)
    {
        _client = client;
        _tracker = tracker;
        _snackbar = snackbar;
        _guard = guard;
        _errors = errors;
        _changes = changes;

        // The list here is a copy of the server's, so it is re-read whenever anything that
        // shows on it changes: an expense written from any page, or a group renamed, which
        // changes the group tag on every one of its rows.
        _changes.TransactionsChanged += RefreshAsync;
        _changes.GroupsChanged += RefreshAsync;

        IsReadyTask = Task.Run(async () =>
        {
            if (tracker.Transactions is not null) return;
            await RefreshAsync();
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

    private Task RefreshAsync() => _guard.RunAsync(() => LoadAsync(), "your expenses");

    // Always a new list, never the old one edited in place: a component that was handed
    // the previous list, the data grid among them, only looks again when the reference
    // changes.
    private async Task LoadAsync(CancellationToken ct = default)
    {
        // Reads one large page while the page states still keep whole lists. The grid and
        // the tiles that would use the rest of the contract -- a page the person chose, and
        // the summary beside it -- come next; this keeps what is on screen correct in the
        // meantime rather than showing a first page as though it were everything.
        var page = await _client.GetTransactionsAsync(pageSize: PageRequest.MaxPageSize, cancellationToken: ct);

        Transactions = page.Items.ToList();
    }

    // Every write below runs through the presenter: a refusal from the API becomes an
    // error snackbar naming the reason, a lost session becomes a sign-in, and the caller
    // gets false instead of an exception it would have had to catch itself. Once the write
    // has landed it is announced rather than applied here by hand, so this list and the
    // groups page's are re-read from the same source and cannot drift apart.

    public Task<bool> CreateAsync(CreateTransactionRequest request, CancellationToken ct = default) =>
        _errors.TryAsync(async () =>
        {
            await _client.CreateTransactionAsync(request, ct);
            _snackbar.Add("Transaction created successfully.", Severity.Success);
            await _changes.NotifyTransactionsChangedAsync();
        }, "Could not save the expense.");

    public Task<bool> UpdateAsync(TransactionResponse transaction, JsonPatchDocument<UpdateTransactionRequest> patch,
        CancellationToken ct = default) =>
        _errors.TryAsync(async () =>
        {
            await _client.UpdateTransactionAsync(transaction.Id, patch, ct);
            _snackbar.Add("Transaction updated successfully.", Severity.Success);
            await _changes.NotifyTransactionsChangedAsync();
        }, "Could not update the expense.");

    public Task<bool> DeleteAsync(TransactionResponse transaction, CancellationToken ct = default) =>
        _errors.TryAsync(async () =>
        {
            await _client.DeleteTransactionAsync(transaction.Id, ct);
            _snackbar.Add("Transaction deleted successfully.", Severity.Success);
            await _changes.NotifyTransactionsChangedAsync();
        }, "Could not delete the expense.");
}

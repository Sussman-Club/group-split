using GroupSplit.App.Shared.Models;
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

        // What is held here is a copy of the server's, so it is re-read whenever anything
        // that shows on it changes: an expense written from any page, or a group renamed,
        // which changes the group tag on every one of its rows.
        _changes.TransactionsChanged += RefreshAsync;
        _changes.GroupsChanged += RefreshAsync;

        IsReadyTask = Task.Run(async () =>
        {
            if (tracker.Page is not null && tracker.Summary is not null) return;
            await RefreshAsync();
        });
    }

    public PagedResponse<TransactionResponse>? Page
    {
        get => _tracker.Page;
        private set => _tracker.Page = value;
    }

    public TransactionQuery Query
    {
        get => _tracker.Query;
        private set => _tracker.Query = value;
    }

    public TransactionSummaryResponse? Summary
    {
        get => _tracker.Summary;
        private set => _tracker.Summary = value;
    }

    public TransactionSummaryResponse? MonthSummary
    {
        get => _tracker.MonthSummary;
        private set => _tracker.MonthSummary = value;
    }

    public TransactionSummaryResponse? MatchesSummary
    {
        get => _tracker.MatchesSummary;
        private set => _tracker.MatchesSummary = value;
    }

    public event Action? OnTransactionsChanged;

    /// <summary>
    /// Announced once a read has finished rather than as each part of it lands. A page and
    /// the three figures beside it are one answer, and a page told four times would reload
    /// its rows four times over.
    /// </summary>
    private void Announce() => OnTransactionsChanged?.Invoke();

    public Task LoadAsync(TransactionQuery query, CancellationToken cancellationToken = default)
    {
        // The grid asks for its state whenever it is rendered, and the first thing it asks
        // for is what this was built holding. Answering from what is held keeps that from
        // being a second request for the same page.
        if (query == Query && Page is not null)
            return Task.CompletedTask;

        return _guard.RunAsync(async () =>
        {
            await ReadPageAsync(query, cancellationToken);
            Announce();
        }, "your expenses");
    }

    /// <summary>
    /// Re-reads the page in hand and the figures beside it. Unlike <see cref="LoadAsync"/>
    /// this asks again for the query it already answered, because the answer is what
    /// changed.
    /// </summary>
    private Task RefreshAsync() =>
        _guard.RunAsync(async () =>
        {
            await ReadPageAsync(Query);
            await ReadSummariesAsync();
            Announce();
        }, "your expenses");

    // Every read below assigns a new object rather than editing the one held: a component
    // handed the previous page, the data grid among them, only looks again when the
    // reference changes.

    private async Task ReadPageAsync(TransactionQuery query, CancellationToken ct = default)
    {
        var range = query.EffectiveRange;

        var page = await _client.GetTransactionsAsync(
            from: range.From,
            to: range.To,
            search: query.Search,
            sortBy: query.SortBy,
            sortDescending: query.SortDescending,
            page: query.Page,
            pageSize: query.PageSize,
            cancellationToken: ct);

        Query = query;
        Page = page;

        // What the page is one of, totalled: the figure beside a narrowed listing has to
        // describe the same narrowing. Only worth a request while something is narrowing
        // it -- otherwise it is the all-time summary, which is already read.
        MatchesSummary = string.IsNullOrWhiteSpace(query.Search) && range.IsAllTime
            ? null
            : await _client.GetTransactionsSummaryAsync(
                from: range.From, to: range.To, search: query.Search, cancellationToken: ct);
    }

    /// <summary>
    /// The figures the tiles show. They come from the server because a page cannot add
    /// itself up -- twenty-five rows of two hundred total to the wrong number.
    /// </summary>
    private async Task ReadSummariesAsync(CancellationToken ct = default)
    {
        var firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var from = new DateTimeOffset(firstOfMonth, TimeZoneInfo.Local.GetUtcOffset(firstOfMonth));
        var to = from.AddMonths(1).AddTicks(-1);

        Summary = await _client.GetTransactionsSummaryAsync(cancellationToken: ct);
        MonthSummary = await _client.GetTransactionsSummaryAsync(from: from, to: to, cancellationToken: ct);
    }

    // Every write below runs through the presenter: a refusal from the API becomes an
    // error snackbar naming the reason, a lost session becomes a sign-in, and the caller
    // gets false instead of an exception it would have had to catch itself. Once the write
    // has landed it is announced rather than applied here by hand, so this page and the
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

using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Transactions;

public class TransactionsPageStateService : ITransactionsPageStateService
{
    private readonly ITransactionsClient _client;
    private readonly TransactionsTracker _tracker;
    private readonly LoadGuard _guard;
    private readonly DataChangeNotifier _changes;
    private readonly ITransactionCommands _commands;
    private readonly LocalClock _clock;

    public Task IsReadyTask { get; }

    public TransactionsPageStateService(ITransactionsClient client,
        TransactionsTracker tracker,
        LoadGuard guard,
        DataChangeNotifier changes,
        ITransactionCommands commands,
        LocalClock clock)
    {
        _client = client;
        _tracker = tracker;
        _guard = guard;
        _changes = changes;
        _commands = commands;
        _clock = clock;

        // What is held here is a copy of the server's, so it is re-read whenever anything
        // that shows on it changes: an expense written from any page, or a group renamed,
        // which changes the group tag on every one of its rows.
        _changes.TransactionsChanged += RefreshAsync;
        _changes.GroupsChanged += RefreshAsync;

        // Started here rather than on a pool thread: the announcement this ends in is what
        // the page re-renders on, and under interactive server rendering that has to reach
        // the circuit's own thread. A first read the prerender already persisted is not
        // read again.
        IsReadyTask = tracker.Page is not null && tracker.Summary is not null
            ? Task.CompletedTask
            : RefreshAsync();
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

    /// <summary>
    /// What was last asked for, which is not what is held until the answer arrives.
    /// </summary>
    /// <remarks>
    /// Two spans picked in quick succession are two requests, and the second can be
    /// answered first: the reported symptom was the right figures appearing and then the
    /// previous ones coming back over them, which is the older answer being written down as
    /// though it were the current one. Kept so an answer can be checked against the
    /// question in force before it is shown, the way the group's tab already does.
    /// </remarks>
    private TransactionQuery? _asked;

    public async Task LoadAsync(TransactionQuery query, CancellationToken cancellationToken = default)
    {
        // The grid asks for its state whenever it is rendered, and the first thing it asks
        // for is what this was built holding. Answering from what is held keeps that from
        // being a second request for the same page -- and from what is on its way, so a
        // second render mid-flight does not ask again either.
        if (Page is not null && (query == Query || query == _asked))
            return;

        _asked = query;

        var done = await _guard.RunAsync(async () =>
        {
            await ReadPageAsync(query, cancellationToken);
            Announce();
        }, "your expenses");

        // A failure is not an answer, so the same question has to be askable again --
        // otherwise the retry would be taken for the one already in flight.
        if (!done && _asked == query)
            _asked = Query;
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
        var bounds = range.Bounds(_clock.Offset, _clock.Today);

        var page = await _client.GetTransactionsAsync(
            from: bounds.From,
            to: bounds.To,
            personal: query.Personal,
            search: query.Search,
            sortBy: query.SortBy,
            sortDescending: query.SortDescending,
            page: query.Page,
            pageSize: query.PageSize,
            cancellationToken: ct);

        // What the page is one of, totalled: the figure beside a narrowed listing has to
        // describe the same narrowing. Only worth a request while something is narrowing
        // it -- otherwise it is the all-time summary, which is already read.
        var matches = string.IsNullOrWhiteSpace(query.Search) && range.IsAllTime && query.Personal is null
            ? null
            : await _client.GetTransactionsSummaryAsync(
                from: bounds.From, to: bounds.To, personal: query.Personal, search: query.Search,
                cancellationToken: ct);

        // Something has been asked for since this went out. That answer is the one the page
        // is waiting for, and this one is about a span nobody is looking at any more --
        // written down here it would put the previous figures back over the current ones.
        //
        // The three go down together, and only here: a page and the summary beside it that
        // came from two different questions are worse than either answer on its own.
        if (_asked is not null && _asked != query)
            return;

        Query = query;
        Page = page;
        MatchesSummary = matches;
    }

    /// <summary>
    /// The figures the tiles show. They come from the server because a page cannot add
    /// itself up -- twenty-five rows of two hundred total to the wrong number.
    /// </summary>
    private async Task ReadSummariesAsync(CancellationToken ct = default)
    {
        // "This month" is the person's month, resolved the same way the chips resolve theirs.
        var month = new DateFilter(DateFilterPreset.ThisMonth).Bounds(_clock.Offset, _clock.Today);

        Summary = await _client.GetTransactionsSummaryAsync(cancellationToken: ct);
        MonthSummary = await _client.GetTransactionsSummaryAsync(from: month.From, to: month.To, cancellationToken: ct);
    }

    // Every write below is one line, because a write is not this class's job: the commands
    // own the call, the message and the announcement, and a dialog making the same change
    // makes it exactly the same way. What is left here is the reading. A refusal from the
    // API becomes an error snackbar naming the reason, a lost session becomes a sign-in,
    // and the caller
    // gets false instead of an exception it would have had to catch itself. Once the write
    // has landed it is announced rather than applied here by hand, so this page and the
    // groups page's are re-read from the same source and cannot drift apart.

    public Task<bool> CreateAsync(CreateTransactionRequest request, CancellationToken ct = default) =>
        _commands.CreateAsync(request, ct);

    public Task<bool> UpdateAsync(TransactionResponse transaction, JsonPatchDocument<UpdateTransactionRequest> patch,
        CancellationToken ct = default) =>
        _commands.UpdateAsync(transaction.Id, patch, transaction.Name, ct);

    public Task<bool> DeleteAsync(TransactionResponse transaction, CancellationToken ct = default) =>
        _commands.DeleteAsync(transaction.Id, transaction.Name, ct: ct);
}

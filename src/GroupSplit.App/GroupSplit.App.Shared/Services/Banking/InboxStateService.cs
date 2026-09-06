using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Banking;

public interface IInboxStateService
{
    /// <summary>The rows currently shown, for the filter in force.</summary>
    IReadOnlyList<BankTransactionResponse> Rows { get; }

    /// <summary>How many rows are waiting, whatever the filter shows. What the nav badge reads.</summary>
    int NewCount { get; }

    /// <summary>Which rows the page is asking for.</summary>
    InboxStatus Filter { get; }

    /// <summary>The linked banks, and whether linking one is possible at all here.</summary>
    BankConnectionsResponse? Connections { get; }

    /// <summary>Completes once the first read has finished, so a page can await it before rendering.</summary>
    Task IsReadyTask { get; }

    event Action? OnChanged;

    Task SetFilterAsync(InboxStatus status, CancellationToken ct = default);

    /// <summary>
    /// Asks for the rows as well as the count, from here on. The nav badge needs only the
    /// count, so the rows are not read until a page says it wants them.
    /// </summary>
    Task LoadRowsAsync(CancellationToken ct = default);

    /// <summary>Re-reads everything this holds.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// What the inbox page and the nav badge both read.
/// </summary>
/// <remarks>
/// One service rather than two, because the badge and the page are two views of the same
/// thing and two services would each fetch the count. It follows the shape the other page
/// states already have: it subscribes to the notifier, re-reads when a write announces
/// itself, and assigns a new list each time rather than editing one in place.
/// <para>
/// The summary is read eagerly, because the badge is on every page and the number is one
/// integer; the rows wait until somebody opens the inbox.
/// </para>
/// </remarks>
public sealed class InboxStateService : IInboxStateService, IDisposable
{
    private readonly IInboxClient _inbox;
    private readonly IBankConnectionsClient _connections;
    private readonly LoadGuard _guard;
    private readonly DataChangeNotifier _changes;

    private bool _rowsWanted;

    public InboxStateService(
        IInboxClient inbox,
        IBankConnectionsClient connections,
        LoadGuard guard,
        DataChangeNotifier changes)
    {
        _inbox = inbox;
        _connections = connections;
        _guard = guard;
        _changes = changes;

        _changes.BankDataChanged += RefreshAsync;

        // An expense deleted elsewhere frees the row it was filed from, so the inbox can
        // disagree with the ledger without this.
        _changes.TransactionsChanged += RefreshAsync;

        IsReadyTask = Task.Run(() => RefreshAsync());
    }

    public IReadOnlyList<BankTransactionResponse> Rows { get; private set; } = [];

    public int NewCount { get; private set; }

    public InboxStatus Filter { get; private set; } = InboxStatus.New;

    public BankConnectionsResponse? Connections { get; private set; }

    public Task IsReadyTask { get; }

    public event Action? OnChanged;

    public async Task SetFilterAsync(InboxStatus status, CancellationToken ct = default)
    {
        if (Filter == status)
            return;

        Filter = status;
        _rowsWanted = true;

        await RefreshAsync(ct);
    }

    /// <inheritdoc />
    public Task LoadRowsAsync(CancellationToken ct = default)
    {
        _rowsWanted = true;
        return RefreshAsync(ct);
    }

    private Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync(CancellationToken ct = default) =>
        _guard.RunAsync(async () =>
        {
            var summary = await _inbox.GetInboxSummaryAsync(ct);
            var connections = await _connections.GetBankConnectionsAsync(ct);

            NewCount = summary.NewCount;
            Connections = connections;

            if (_rowsWanted)
            {
                var page = await _inbox.GetInboxAsync(Filter, null, null, 1, 100, ct);
                Rows = page.Items;
            }

            Announce();
        }, "your bank inbox");

    private void Announce() => OnChanged?.Invoke();

    public void Dispose()
    {
        _changes.BankDataChanged -= RefreshAsync;
        _changes.TransactionsChanged -= RefreshAsync;
    }
}

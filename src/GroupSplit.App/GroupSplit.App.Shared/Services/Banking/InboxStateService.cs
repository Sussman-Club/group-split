using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Banking;

public interface IInboxStateService
{
    /// <summary>The rows currently shown, for the filter in force.</summary>
    IReadOnlyList<BankTransactionResponse> Rows { get; }

    /// <summary>How many rows are waiting, whatever the filter shows. What the nav badge reads.</summary>
    int NewCount { get; }

    /// <summary>
    /// How many of those look like an expense already recorded, or null if nobody has asked
    /// for that count.
    /// </summary>
    /// <remarks>
    /// Null and zero are different answers. Working it out means running the matcher over
    /// every waiting row, which is too much for the badge on every page, so it is asked for
    /// only where it is said out loud -- see <see cref="WantDuplicateCountAsync"/>.
    /// </remarks>
    int? PossibleDuplicates { get; }

    /// <summary>Which rows the page is asking for.</summary>
    InboxStatus Filter { get; }

    /// <summary>
    /// The span of days the page is asking for, as the person chose it. All time until they
    /// narrow it: a bank sends months of rows, and sorting them out a month at a time is how
    /// anybody actually gets through a backlog.
    /// </summary>
    DateFilter Range { get; }

    /// <summary>How many rows the filter in force has in total, not how many are loaded.</summary>
    int TotalCount { get; }

    /// <summary>Whether the server has rows this has not asked for yet.</summary>
    bool HasMore { get; }

    /// <summary>The linked banks, and whether linking one is possible at all here.</summary>
    BankConnectionsResponse? Connections { get; }

    /// <summary>
    /// Reads what this holds, once, and completes when that first read is done. Calling it
    /// again is free.
    /// </summary>
    /// <remarks>
    /// Explicit rather than something the constructor starts, because the nav renders for
    /// signed-out visitors too and a read on their behalf answers 401 -- which the error
    /// presenter quite correctly turns into a trip to the sign-in page. Nothing is fetched
    /// until a caller that knows somebody is signed in asks for it.
    /// </remarks>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Asks for the duplicate count as well as the waiting count, from here on.
    /// </summary>
    Task WantDuplicateCountAsync(CancellationToken ct = default);

    event Action? OnChanged;

    Task SetFilterAsync(InboxStatus status, CancellationToken ct = default);

    /// <summary>Narrows the rows to a span of days, or widens them back to all of them.</summary>
    Task SetRangeAsync(DateFilter range, CancellationToken ct = default);

    /// <summary>
    /// Asks for the rows as well as the count, from here on. The nav badge needs only the
    /// count, so the rows are not read until a page says it wants them.
    /// </summary>
    Task LoadRowsAsync(CancellationToken ct = default);

    /// <summary>Asks for the next page and adds it to what is already shown.</summary>
    Task LoadMoreAsync(CancellationToken ct = default);

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
    /// <summary>
    /// Rows per page. A bank sends a few a day, so this is a couple of weeks at a time.
    /// </summary>
    private const int PageSize = 25;

    private readonly IInboxClient _inbox;
    private readonly IBankConnectionsClient _connections;
    private readonly LoadGuard _guard;
    private readonly DataChangeNotifier _changes;
    private readonly LocalClock _clock;

    private readonly Lock _lock = new();

    private Task? _loaded;
    private bool _rowsWanted;
    private bool _duplicatesWanted;

    /// <summary>
    /// How many pages the inbox is showing. Re-read as one query rather than kept as a
    /// list, so a row filed on another tab disappears from what is on screen instead of
    /// leaving a hole that only a reload closes.
    /// </summary>
    private int _pages = 1;

    public InboxStateService(
        IInboxClient inbox,
        IBankConnectionsClient connections,
        LoadGuard guard,
        DataChangeNotifier changes,
        LocalClock clock)
    {
        _inbox = inbox;
        _connections = connections;
        _guard = guard;
        _changes = changes;
        _clock = clock;

        _changes.BankDataChanged += RefreshAsync;

        // An expense deleted elsewhere frees the row it was filed from, so the inbox can
        // disagree with the ledger without this.
        _changes.TransactionsChanged += RefreshAsync;
    }

    public IReadOnlyList<BankTransactionResponse> Rows { get; private set; } = [];

    public int NewCount { get; private set; }

    public int? PossibleDuplicates { get; private set; }

    public InboxStatus Filter { get; private set; } = InboxStatus.New;

    public DateFilter Range { get; private set; } = DateFilter.AllTime;

    public int TotalCount { get; private set; }

    public bool HasMore => Rows.Count < TotalCount;

    public BankConnectionsResponse? Connections { get; private set; }

    public event Action? OnChanged;

    /// <inheritdoc />
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Two readers -- the sidebar's nav and the mobile tab bar's -- render at once,
            // so the first read is started once and both await the same task.
            return _loaded ??= RefreshAsync(ct);
        }
    }

    public async Task SetFilterAsync(InboxStatus status, CancellationToken ct = default)
    {
        if (Filter == status)
            return;

        Filter = status;
        _rowsWanted = true;

        // Back to the first page: the one being shown belongs to the filter being left.
        _pages = 1;

        await RefreshAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetRangeAsync(DateFilter range, CancellationToken ct = default)
    {
        if (Range == range)
            return;

        Range = range;
        _rowsWanted = true;

        // As above: pages three and four of a wider span are not pages of a narrower one.
        _pages = 1;

        await RefreshAsync(ct);
    }

    /// <inheritdoc />
    public Task WantDuplicateCountAsync(CancellationToken ct = default)
    {
        if (_duplicatesWanted && PossibleDuplicates is not null)
            return Task.CompletedTask;

        _duplicatesWanted = true;

        lock (_lock)
            _loaded ??= Task.CompletedTask;

        return RefreshAsync(ct);
    }

    /// <inheritdoc />
    public Task LoadRowsAsync(CancellationToken ct = default)
    {
        _rowsWanted = true;

        lock (_lock)
            _loaded ??= Task.CompletedTask;

        return RefreshAsync(ct);
    }

    /// <inheritdoc />
    public Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!HasMore)
            return Task.CompletedTask;

        _pages++;

        return RefreshAsync(ct);
    }

    private Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync(CancellationToken ct = default) =>
        _guard.RunAsync(async () =>
        {
            var summary = await _inbox.GetInboxSummaryAsync(_duplicatesWanted ? true : null, ct);
            var connections = await _connections.GetBankConnectionsAsync(ct);

            NewCount = summary.NewCount;
            PossibleDuplicates = summary.PossibleDuplicates;
            Connections = connections;

            if (_rowsWanted)
            {
                // A bank row is dated by the day the bank put on it, so the span is asked
                // for as days. Which month "this month" is remains the person's question,
                // which is what the clock is for.
                var days = Range.Days(_clock.Today);

                var page = await _inbox.GetInboxAsync(
                    status: Filter,
                    from: days.From,
                    to: days.To,
                    sortBy: null,
                    sortDescending: null,
                    page: 1,
                    pageSize: PageSize * _pages,
                    cancellationToken: ct);

                Rows = page.Items;
                TotalCount = page.TotalCount;
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

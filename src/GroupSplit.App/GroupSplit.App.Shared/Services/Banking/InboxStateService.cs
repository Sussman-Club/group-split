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
    /// Null and zero are different answers. Working it out means comparing every waiting row
    /// with the expenses around it, which the badge on every page has no use for, so it is
    /// asked for only where it is said out loud -- see <see cref="WantDuplicateCountAsync"/>.
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

    /// <summary>How many pages of rows are shown: one, plus one per "Load more".</summary>
    int Pages { get; }

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

    /// <summary>
    /// Asks for the rows of one filter, span and number of pages, in one read -- what a URL
    /// asks for when the page opens on it.
    /// </summary>
    Task LoadRowsAsync(InboxStatus status, DateFilter range, int pages, CancellationToken ct = default);

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

    private const int MaxPages = 40;

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
    /// How many pages the inbox is showing. "Load more" reads only the next one and adds
    /// it; a refresh re-reads every page shown, so a row filed on another tab disappears
    /// from what is on screen instead of leaving a hole that only a reload closes.
    /// </summary>
    /// <remarks>
    /// Page by page, never as one page the size of all of them: that re-downloaded and
    /// re-matched everything already shown on every "Load more", and stopped growing at
    /// eight pages, where it met the server's <see cref="PageRequest.MaxPageSize"/>.
    /// </remarks>
    private int _pages = 1;

    /// <summary>
    /// Counts reads. Two filters picked in quick succession are two requests, and the
    /// first can answer last; its rows are the wrong ones under the second's tab, so an
    /// answer is written down only if nothing has been asked for since.
    /// </summary>
    private int _version;

    /// <summary>
    /// The read whose answer is the one on screen. Behind <see cref="_version"/> while a
    /// read is out, or when the last one failed -- and then the rows shown are not a safe
    /// base for "Load more" to add a page to.
    /// </summary>
    private int _written;

    /// <summary>
    /// What has been asked about each row and what came back, null for no suggestion.
    /// </summary>
    /// <remarks>
    /// The rows are read again after every write anywhere in the app, and asking the model
    /// about the same twenty-five rows each time would be twenty-five calls per filing. A
    /// row is asked about once; every later read of it is given the answer already had.
    /// </remarks>
    private readonly Dictionary<Guid, FilingSuggestionResponse?> _suggestions = [];

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

    public int Pages => _pages;

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
    public Task LoadRowsAsync(InboxStatus status, DateFilter range, int pages, CancellationToken ct = default)
    {
        Filter = status;
        Range = range;

        // A link asks for as many pages as somebody once loaded, which is never thousands;
        // the cap is there so a hand-edited one cannot ask for thousands of reads.
        _pages = Math.Clamp(pages, 1, MaxPages);

        return LoadRowsAsync(ct);
    }

    /// <inheritdoc />
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!HasMore)
            return;

        var page = ++_pages;

        // A read is still out, or the last one failed. Adding a page to rows that are about
        // to be replaced, or that are not the answer to the current question, is how a page
        // gets skipped; reading everything shown, this page included, cannot skip one.
        if (_written != _version)
        {
            await RefreshAsync(ct);
            return;
        }

        var loaded = await _guard.RunAsync(async () =>
        {
            var version = ++_version;
            var filter = Filter;
            var range = Range;

            var next = await PageAsync(filter, range.Days(_clock.Today), page, ct);

            if (version != _version)
                return;

            // The rows are paged by offset, so a row arriving since the last read pushes the
            // last one shown onto this page as well. Shown once.
            var shown = Rows.Select(row => row.Id).ToHashSet();

            Rows = WithSuggestions([.. Rows, .. next.Items.Where(row => !shown.Contains(row.Id))]);
            TotalCount = next.TotalCount;
            _written = version;

            Announce();
            _ = SuggestAsync();
        }, "your bank inbox");

        // Not loaded, so not shown: the next "Load more" asks for this page again rather
        // than the one after it.
        if (!loaded && _pages == page)
            _pages--;
    }

    /// <summary>One page of the rows the filter and span ask for.</summary>
    private Task<PagedResponseOfBankTransactionResponse> PageAsync(
        InboxStatus filter, DayBounds days, int page, CancellationToken ct) =>
        _inbox.GetInboxAsync(
            status: filter,
            from: days.From,
            to: days.To,
            sortBy: null,
            sortDescending: null,
            page: page,
            pageSize: PageSize,
            cancellationToken: ct);

    /// <summary>
    /// Pages one to <paramref name="pages"/>, as one list. The first is read alone, because
    /// it says how many there are; the rest that exist are read together.
    /// </summary>
    private async Task<(IReadOnlyList<BankTransactionResponse> Rows, int TotalCount)> PagesAsync(
        InboxStatus filter, DayBounds days, int pages, CancellationToken ct)
    {
        var first = await PageAsync(filter, days, 1, ct);
        var existing = Math.Min(pages, Math.Max(1, (first.TotalCount + PageSize - 1) / PageSize));

        var rest = await Task.WhenAll(Enumerable.Range(2, existing - 1)
            .Select(page => PageAsync(filter, days, page, ct)));

        // A row arriving between two of the reads shifts every page after it by one, which
        // would show the row at the boundary twice.
        var rows = first.Items
            .Concat(rest.SelectMany(page => page.Items))
            .DistinctBy(row => row.Id)
            .ToList();

        return (rows, rest.Length > 0 ? rest[^1].TotalCount : first.TotalCount);
    }

    private Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync(CancellationToken ct = default) =>
        _guard.RunAsync(async () =>
        {
            var version = ++_version;

            // Read once, here: the filter and the span can move while this is in flight,
            // and the rows have to be the answer to one question.
            var filter = Filter;
            var range = Range;
            var pages = _pages;

            var summary = await _inbox.GetInboxSummaryAsync(_duplicatesWanted ? true : null, ct);
            var connections = await _connections.GetBankConnectionsAsync(ct);

            (IReadOnlyList<BankTransactionResponse> Rows, int TotalCount)? shown = null;

            if (_rowsWanted)
            {
                // A bank row is dated by the day the bank put on it, so the span is asked
                // for as days. Which month "this month" is remains the person's question,
                // which is what the clock is for.
                shown = await PagesAsync(filter, range.Days(_clock.Today), pages, ct);
            }

            // Something has been asked for since this went out. That answer is the one
            // the page is waiting for; this one would put the previous filter's rows back.
            if (version != _version)
                return;

            NewCount = summary.NewCount;
            PossibleDuplicates = summary.PossibleDuplicates;
            Connections = connections;

            if (shown is { } read)
            {
                Rows = WithSuggestions(read.Rows);
                TotalCount = read.TotalCount;
            }

            _written = version;

            Announce();
            _ = SuggestAsync();
        }, "your bank inbox");

    /// <summary>The rows, each carrying the suggestion already had for it.</summary>
    private IReadOnlyList<BankTransactionResponse> WithSuggestions(IReadOnlyList<BankTransactionResponse> rows) =>
        [.. rows.Select(row => _suggestions.GetValueOrDefault(row.Id) is { } suggestion
            ? row with { Suggestion = suggestion }
            : row)];

    /// <summary>
    /// Asks where the waiting rows on screen probably go, after they are shown rather than
    /// before: the inbox never waits on the model, and a suggestion appears on its row when
    /// it arrives.
    /// </summary>
    /// <remarks>
    /// Not for a row that could already be recorded -- the question there is whether to
    /// record anything -- nor for money coming in, which cannot be filed. A failure is the
    /// same as no suggestion, which is how the inbox looked before there were any.
    /// </remarks>
    private async Task SuggestAsync()
    {
        var wanted = Rows
            .Where(row => row.Status == InboxStatus.New
                          && !row.IsCredit
                          && row.PossibleDuplicates.Count == 0
                          && !_suggestions.ContainsKey(row.Id))
            .Select(row => row.Id)
            .ToList();

        if (wanted.Count == 0)
            return;

        foreach (var id in wanted)
            _suggestions[id] = null;

        foreach (var chunk in wanted.Chunk(SuggestFilingsRequest.MaxRows))
        {
            try
            {
                var answers = await _inbox.SuggestBankTransactionFilingsAsync(new SuggestFilingsRequest
                {
                    Rows = [.. chunk.Select(id => new SuggestFilingRow { Id = id })]
                });

                foreach (var answer in answers)
                    _suggestions[answer.BankTransactionId] = answer;
            }
            catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
            {
                continue;
            }

            Rows = WithSuggestions(Rows);
            Announce();
        }
    }

    private void Announce() => OnChanged?.Invoke();

    public void Dispose()
    {
        _changes.BankDataChanged -= RefreshAsync;
        _changes.TransactionsChanged -= RefreshAsync;
    }
}

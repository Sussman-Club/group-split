using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What "Load more" asks the server for.
/// </summary>
/// <remarks>
/// It used to ask for one page the size of every page shown -- 25, then 50, then 75 --
/// re-downloading and re-matching everything already on screen each time. And past eight
/// pages that one page was larger than <see cref="PageRequest.MaxPageSize"/>, so the server
/// clamped it to 200, the list stopped growing, and the button stayed.
/// </remarks>
public class InboxLoadMoreTest
{
    private const int PageSize = 25;

    private readonly Mock<IInboxClient> _inbox = new();
    private readonly Mock<IBankConnectionsClient> _connections = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly InboxStateService _state;

    /// <summary>Every row the server holds, newest first, as it would page them.</summary>
    private readonly List<BankTransactionResponse> _rows;

    /// <summary>Each page read, as (page, page size).</summary>
    private readonly List<(int Page, int PageSize)> _asked = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public InboxLoadMoreTest()
    {
        // Ten pages and a bit: well past the eight a 200-row ceiling allowed.
        _rows = [.. Enumerable.Range(0, 260).Select(index => Row($"Row {index}"))];

        _inbox
            .Setup(client => client.GetInboxSummaryAsync(It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InboxSummaryResponse(_rows.Count));

        _inbox
            .Setup(client => client.GetInboxAsync(It.IsAny<InboxStatus?>(), It.IsAny<DateOnly?>(),
                It.IsAny<DateOnly?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxStatus? _, DateOnly? _, DateOnly? _, string _, bool? _, int? page, int? pageSize,
                CancellationToken _) =>
            {
                // As the server applies it: clamped to its ceiling, whatever was asked.
                var request = new PageRequest(page ?? 1, pageSize ?? PageRequest.DefaultPageSize).Normalized();

                lock (_asked)
                    _asked.Add((request.Page, request.PageSize));

                var items = _rows.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();

                return new PagedResponseOfBankTransactionResponse(items, request.Page, request.PageSize, _rows.Count);
            });

        _connections
            .Setup(client => client.GetBankConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BankConnectionsResponse(true, []));

        var presenter = new ApiErrorPresenter(new Mock<NavigationManager>().Object, Mock.Of<ISnackbar>());

        _state = new InboxStateService(_inbox.Object, _connections.Object, new LoadGuard(presenter),
            _changes, new LocalClock(Mock.Of<IJSRuntime>()));
    }

    [Fact]
    public async Task Load_more_reads_only_the_next_page_and_adds_it()
    {
        await _state.LoadRowsAsync(Ct);
        _asked.Clear();

        await _state.LoadMoreAsync(Ct);

        Assert.Equal(new[] { (2, PageSize) }, _asked);
        Assert.Equal(_rows.Take(2 * PageSize).Select(row => row.Id), _state.Rows.Select(row => row.Id));
        Assert.Equal(2, _state.Pages);
    }

    [Fact]
    public async Task Load_more_keeps_going_past_eight_pages_until_everything_is_shown()
    {
        await _state.LoadRowsAsync(Ct);

        while (_state.HasMore)
            await _state.LoadMoreAsync(Ct);

        Assert.Equal(_rows.Select(row => row.Id), _state.Rows.Select(row => row.Id));
        Assert.Equal(11, _state.Pages);

        // One read per page, each of one page's worth.
        Assert.Equal(Enumerable.Range(1, 11).Select(page => (page, PageSize)), _asked);
    }

    /// <summary>
    /// A write elsewhere re-reads what is on screen -- every page of it, so a row filed on
    /// another tab leaves the list -- and not more than is on screen.
    /// </summary>
    [Fact]
    public async Task A_refresh_rereads_the_pages_shown_one_page_at_a_time()
    {
        await _state.LoadRowsAsync(Ct);
        await _state.LoadMoreAsync(Ct);
        await _state.LoadMoreAsync(Ct);

        _rows.RemoveAt(0);
        _asked.Clear();

        await _changes.NotifyBankDataChangedAsync();

        Assert.Equal(new[] { (1, PageSize), (2, PageSize), (3, PageSize) }, _asked.Order());
        Assert.Equal(_rows.Take(3 * PageSize).Select(row => row.Id), _state.Rows.Select(row => row.Id));
    }

    /// <summary>
    /// The rows are paged by offset, so one arriving between two reads pushes the last row
    /// shown onto the next page as well. It is shown once.
    /// </summary>
    [Fact]
    public async Task A_row_pushed_onto_the_next_page_is_not_shown_twice()
    {
        await _state.LoadRowsAsync(Ct);

        _rows.Insert(0, Row("Arrived since"));

        await _state.LoadMoreAsync(Ct);

        Assert.Equal(_state.Rows.Count, _state.Rows.Select(row => row.Id).Distinct().Count());
        Assert.Equal(2 * PageSize - 1, _state.Rows.Count);
    }

    private static BankTransactionResponse Row(string title) =>
        new(Guid.NewGuid(), new DateOnly(2026, 9, 1), 30m, "USD", title.ToUpperInvariant(), title,
            null, null, null, null, null, null, null, false, InboxStatus.New, null, null,
            "Everyday", "Fake Bank");
}

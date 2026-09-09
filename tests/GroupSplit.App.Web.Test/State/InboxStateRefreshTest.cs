using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The inbox is read in two places -- the page and the badge in the nav -- and written from
/// a third, the dialog. This pins that a write through the commands is seen by both readers
/// without anybody reloading, and that filing announces itself as an expense as well, since
/// it is the one action that is two things.
/// </summary>
/// <remarks>
/// Real commands and a real state service over mocked clients, like
/// <see cref="PageStateRefreshTest"/>: the write and the re-read it triggers are the thing
/// under test, and the announcement that joins them lives in the command.
/// </remarks>
public class InboxStateRefreshTest
{
    private readonly Mock<IInboxClient> _inboxClient = new();
    private readonly Mock<IBankConnectionsClient> _connectionsClient = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();

    private readonly List<BankTransactionResponse> _rows;
    private readonly IBankCommands _commands;
    private readonly InboxStateService _state;

    private int _transactionsAnnounced;

    /// <summary>The span the last read asked the server for.</summary>
    private DayBounds _asked;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public InboxStateRefreshTest()
    {
        _rows =
        [
            // The first one is showing a suggestion: an expense already recorded that it
            // could be. The second is an ordinary row with nothing to ask about.
            Row("Lidl", 30m) with
            {
                PossibleDuplicates =
                [
                    new ExpenseMatchResponse(Guid.NewGuid(), "Shopping", 28m, "USD",
                        new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero), null, null, "Me", 2m, 0)
                ]
            },
            Row("Blue Bottle", 4.5m)
        ];

        _inboxClient
            .Setup(client => client.GetInboxSummaryAsync(It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InboxSummaryResponse(_rows.Count(row => row.Status == InboxStatus.New)));

        _inboxClient
            .Setup(client => client.GetInboxAsync(It.IsAny<InboxStatus?>(), It.IsAny<DateOnly?>(),
                It.IsAny<DateOnly?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback((InboxStatus? _, DateOnly? from, DateOnly? to, string _, bool? _, int? _, int? _,
                CancellationToken _) => _asked = new DayBounds(from, to))
            .ReturnsAsync((InboxStatus? status, DateOnly? from, DateOnly? to, string _, bool? _, int? _, int? _,
                CancellationToken _) =>
            {
                // The server narrows by the day the row shows, so the fake does too --
                // otherwise a test could pass against a filter the API would not apply.
                var matching = _rows
                    .Where(row => row.Status == (status ?? InboxStatus.New))
                    .Where(row => from is null || row.SpentOn >= from)
                    .Where(row => to is null || row.SpentOn <= to)
                    .ToList();

                return new PagedResponseOfBankTransactionResponse(matching, 1, 100, matching.Count);
            });

        _inboxClient
            .Setup(client => client.IgnoreBankTransactionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, CancellationToken _) => SetStatus(id, InboxStatus.Ignored))
            .Returns(Task.CompletedTask);

        _inboxClient
            .Setup(client => client.RestoreBankTransactionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, CancellationToken _) => SetStatus(id, InboxStatus.New))
            .Returns(Task.CompletedTask);

        _inboxClient
            .Setup(client => client.FileBankTransactionAsync(It.IsAny<Guid>(),
                It.IsAny<FileBankTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, FileBankTransactionRequest request, CancellationToken _) =>
            {
                var row = _rows.Single(candidate => candidate.Id == id);
                SetStatus(id, InboxStatus.Filed);

                return new TransactionResponse
                {
                    Id = Guid.NewGuid(),
                    Name = row.Title,
                    Amount = row.Amount,
                    DateTime = DateTimeOffset.UtcNow,
                    GroupId = request.GroupId
                };
            });

        _inboxClient
            .Setup(client => client.LinkBankTransactionAsync(It.IsAny<Guid>(),
                It.IsAny<LinkBankTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, LinkBankTransactionRequest request, CancellationToken _) =>
            {
                // The expense that was already there, now carrying the row. Nothing new is
                // recorded, which is the whole point of this answer.
                var row = _rows.Single(candidate => candidate.Id == id);
                SetStatus(id, InboxStatus.Filed);

                return new TransactionResponse
                {
                    Id = request.TransactionId,
                    Name = "Dinner",
                    Amount = row.Amount,
                    DateTime = DateTimeOffset.UtcNow
                };
            });

        _inboxClient
            .Setup(client => client.DismissBankTransactionMatchAsync(It.IsAny<Guid>(),
                It.IsAny<DismissBankMatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, DismissBankMatchRequest _, CancellationToken _) => ClearMatches(id))
            .Returns(Task.CompletedTask);

        _connectionsClient
            .Setup(client => client.GetBankConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BankConnectionsResponse(true, []));

        var presenter = new ApiErrorPresenter(Mock.Of<IAuthService>(), new Mock<NavigationManager>().Object,
            _snackbar.Object);

        _commands = new BankCommands(_connectionsClient.Object, _inboxClient.Object, presenter,
            _snackbar.Object, _changes);

        _state = new InboxStateService(_inboxClient.Object, _connectionsClient.Object,
            new LoadGuard(presenter), _changes, new LocalClock(Mock.Of<IJSRuntime>()));

        _changes.TransactionsChanged += () =>
        {
            Interlocked.Increment(ref _transactionsAnnounced);
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task The_badge_counts_what_is_waiting_before_any_page_asks_for_the_rows()
    {
        await _state.EnsureLoadedAsync(Ct);

        Assert.Equal(2, _state.NewCount);

        // The nav needs a number on every page; the rows wait until the inbox is opened.
        Assert.Empty(_state.Rows);
    }

    [Fact]
    public async Task Ignoring_a_row_drops_the_badge_and_the_row_without_a_reload()
    {
        await _state.EnsureLoadedAsync(Ct);
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Blue Bottle");

        await _commands.IgnoreAsync(row.Id, row.Title, Ct);

        Assert.Equal(1, _state.NewCount);
        Assert.DoesNotContain(_state.Rows, candidate => candidate.Id == row.Id);

        // Nothing became an expense, so nothing told the expenses page to re-read.
        Assert.Equal(0, _transactionsAnnounced);
    }

    [Fact]
    public async Task Filing_a_row_tells_the_expenses_pages_as_well_as_the_inbox()
    {
        await _state.EnsureLoadedAsync(Ct);
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Lidl");

        var expense = await _commands.FileAsync(row.Id, new FileBankTransactionRequest { GroupId = Guid.NewGuid() },
            "Home", Ct);

        Assert.NotNull(expense);
        Assert.Equal(1, _state.NewCount);
        Assert.DoesNotContain(_state.Rows, candidate => candidate.Id == row.Id);

        // Filing is the one action that is two things, so both announcements go out.
        Assert.Equal(1, _transactionsAnnounced);
    }

    [Fact]
    public async Task A_restored_row_comes_back_to_what_is_waiting()
    {
        await _state.EnsureLoadedAsync(Ct);
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Lidl");

        await _commands.IgnoreAsync(row.Id, row.Title, Ct);
        Assert.Equal(1, _state.NewCount);

        await _commands.RestoreAsync(row.Id, row.Title, Ct);

        Assert.Equal(2, _state.NewCount);
        Assert.Contains(_state.Rows, candidate => candidate.Id == row.Id);
    }

    [Fact]
    public async Task Switching_the_filter_shows_what_that_filter_holds()
    {
        await _state.EnsureLoadedAsync(Ct);
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Lidl");
        await _commands.IgnoreAsync(row.Id, row.Title, Ct);

        await _state.SetFilterAsync(InboxStatus.Ignored, Ct);

        Assert.Equal(InboxStatus.Ignored, _state.Filter);
        Assert.Equal(["Lidl"], _state.Rows.Select(candidate => candidate.Title));

        // The badge counts what is waiting whatever the page happens to be showing.
        Assert.Equal(1, _state.NewCount);
    }

    [Fact]
    public async Task Attaching_a_row_to_an_expense_takes_it_out_of_the_inbox_and_records_nothing_new()
    {
        await _state.EnsureLoadedAsync(Ct);
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Lidl");
        var already = Guid.NewGuid();

        var expense = await _commands.AttachAsync(row.Id, already, Ct);

        // The expense that came back is the one that was already there.
        Assert.NotNull(expense);
        Assert.Equal(already, expense.Id);

        Assert.Equal(1, _state.NewCount);
        Assert.DoesNotContain(_state.Rows, candidate => candidate.Id == row.Id);

        // A row leaving the inbox and an expense gaining a bank row are both changes the
        // expense pages are showing, so both announcements go out.
        Assert.Equal(1, _transactionsAnnounced);
    }

    [Fact]
    public async Task Saying_a_pair_is_not_the_same_money_leaves_the_row_alone_and_drops_the_suggestion()
    {
        await _state.EnsureLoadedAsync(Ct);
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Lidl");

        Assert.NotEmpty(row.PossibleDuplicates);

        await _commands.DismissMatchAsync(row.Id, row.PossibleDuplicates[0].TransactionId, row.Title, Ct);

        var still = _state.Rows.Single(candidate => candidate.Id == row.Id);

        // Still waiting, still the caller's to file or ignore -- and no longer asking.
        Assert.Equal(InboxStatus.New, still.Status);
        Assert.Empty(still.PossibleDuplicates);
        Assert.Equal(2, _state.NewCount);
        Assert.Equal(0, _transactionsAnnounced);
    }

    private void SetStatus(Guid id, InboxStatus status)
    {
        var index = _rows.FindIndex(row => row.Id == id);
        _rows[index] = _rows[index] with { Status = status };
    }

    // ---- Narrowing to a span of days ---------------------------------------------------
    //
    // Pinned to a custom span rather than a preset: "this month" is whichever month the
    // suite happens to run in, and a test that passes in September and fails in October is
    // worse than no test. Which days a preset resolves to is DateFilterTest's job.

    /// <summary>
    /// The span goes to the server. A client narrowing the rows it was handed could only
    /// ever narrow the page in front of it, which is the same mistake the expenses search
    /// box was written against.
    /// </summary>
    [Fact]
    public async Task Narrowing_to_a_span_asks_the_server_for_that_span()
    {
        await _state.LoadRowsAsync(Ct);

        Assert.Equal(2, _state.Rows.Count);
        Assert.Equal(new DayBounds(null, null), _asked);

        await _state.SetRangeAsync(Custom(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), Ct);

        Assert.Equal(new DayBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), _asked);
        Assert.Equal(2, _state.Rows.Count);
    }

    [Fact]
    public async Task A_span_the_rows_fall_outside_shows_none_of_them()
    {
        await _state.LoadRowsAsync(Ct);

        await _state.SetRangeAsync(Custom(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31)), Ct);

        Assert.Empty(_state.Rows);
        Assert.Equal(0, _state.TotalCount);

        // The badge is not the page: it counts everything waiting, whenever it is from, and
        // narrowing what is on screen does not mean the rest stopped waiting.
        Assert.Equal(2, _state.NewCount);
    }

    /// <summary>Widening back is the same move in reverse, and must leave no span behind.</summary>
    [Fact]
    public async Task Going_back_to_all_time_asks_for_everything_again()
    {
        await _state.LoadRowsAsync(Ct);
        await _state.SetRangeAsync(Custom(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31)), Ct);

        Assert.Empty(_state.Rows);

        await _state.SetRangeAsync(DateFilter.AllTime, Ct);

        Assert.Equal(new DayBounds(null, null), _asked);
        Assert.Equal(2, _state.Rows.Count);
    }

    /// <summary>
    /// The span survives a write. Filing one row of a month being worked through must not
    /// quietly put the other months back on screen.
    /// </summary>
    [Fact]
    public async Task A_write_re_reads_within_the_span_being_shown()
    {
        await _state.LoadRowsAsync(Ct);
        await _state.SetRangeAsync(Custom(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), Ct);

        var row = _state.Rows[0];

        await _commands.IgnoreAsync(row.Id, row.Title, Ct);

        Assert.Equal(new DayBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), _asked);
        Assert.Single(_state.Rows);
    }

    private static DateFilter Custom(DateTime from, DateTime to) =>
        new(DateFilterPreset.Custom, from, to);

    /// <summary>
    /// What the API does after a dismissal: the pair is remembered, so the row comes back
    /// from the next listing with nothing left to suggest.
    /// </summary>
    private void ClearMatches(Guid id)
    {
        var index = _rows.FindIndex(row => row.Id == id);
        _rows[index] = _rows[index] with { PossibleDuplicates = [] };
    }

    private static BankTransactionResponse Row(string merchant, decimal amount) =>
        new(Guid.NewGuid(),
            new DateOnly(2026, 9, 1),
            amount,
            "USD",
            merchant.ToUpperInvariant(),
            merchant,
            "FOOD_AND_DRINK",
            "FOOD_AND_DRINK_GROCERIES",
            null,
            "in store",
            "Lisbon",
            null,
            null,
            false,
            InboxStatus.New,
            null,
            null,
            "Everyday",
            "Fake Bank");
}

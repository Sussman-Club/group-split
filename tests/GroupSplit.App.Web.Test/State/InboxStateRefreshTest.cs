using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public InboxStateRefreshTest()
    {
        _rows =
        [
            Row("Lidl", 30m),
            Row("Blue Bottle", 4.5m)
        ];

        _inboxClient
            .Setup(client => client.GetInboxSummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InboxSummaryResponse(_rows.Count(row => row.Status == InboxStatus.New)));

        _inboxClient
            .Setup(client => client.GetInboxAsync(It.IsAny<InboxStatus?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxStatus? status, string _, bool? _, int? _, int? _, CancellationToken _) =>
            {
                var matching = _rows.Where(row => row.Status == (status ?? InboxStatus.New)).ToList();
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

        _connectionsClient
            .Setup(client => client.GetBankConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BankConnectionsResponse(true, []));

        var presenter = new ApiErrorPresenter(Mock.Of<IAuthService>(), new Mock<NavigationManager>().Object,
            _snackbar.Object);

        _commands = new BankCommands(_connectionsClient.Object, _inboxClient.Object, presenter,
            _snackbar.Object, _changes);

        _state = new InboxStateService(_inboxClient.Object, _connectionsClient.Object,
            new LoadGuard(presenter), _changes);

        _changes.TransactionsChanged += () =>
        {
            Interlocked.Increment(ref _transactionsAnnounced);
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task The_badge_counts_what_is_waiting_before_any_page_asks_for_the_rows()
    {
        await _state.IsReadyTask;

        Assert.Equal(2, _state.NewCount);

        // The nav needs a number on every page; the rows wait until the inbox is opened.
        Assert.Empty(_state.Rows);
    }

    [Fact]
    public async Task Ignoring_a_row_drops_the_badge_and_the_row_without_a_reload()
    {
        await _state.IsReadyTask;
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
        await _state.IsReadyTask;
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
        await _state.IsReadyTask;
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
        await _state.IsReadyTask;
        await _state.LoadRowsAsync(Ct);

        var row = _state.Rows.Single(candidate => candidate.Title == "Lidl");
        await _commands.IgnoreAsync(row.Id, row.Title, Ct);

        await _state.SetFilterAsync(InboxStatus.Ignored, Ct);

        Assert.Equal(InboxStatus.Ignored, _state.Filter);
        Assert.Equal(["Lidl"], _state.Rows.Select(candidate => candidate.Title));

        // The badge counts what is waiting whatever the page happens to be showing.
        Assert.Equal(1, _state.NewCount);
    }

    private void SetStatus(Guid id, InboxStatus status)
    {
        var index = _rows.FindIndex(row => row.Id == id);
        _rows[index] = _rows[index] with { Status = status };
    }

    private static BankTransactionResponse Row(string merchant, decimal amount) =>
        new(Guid.NewGuid(),
            new DateOnly(2026, 9, 1),
            amount,
            "USD",
            merchant.ToUpperInvariant(),
            merchant,
            "FOOD_AND_DRINK",
            false,
            InboxStatus.New,
            null,
            null,
            "Everyday",
            "Fake Bank");
}

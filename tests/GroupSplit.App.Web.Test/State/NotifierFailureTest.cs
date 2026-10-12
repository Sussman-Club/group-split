using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Settling;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// A write that landed, followed by a re-read that did not, in the two state services
/// <see cref="PageStateRefreshTest"/> does not cover: the settle plan and the inbox.
/// </summary>
/// <remarks>
/// Both writes here announce themselves inside the same try that reports their own failure,
/// so a re-read that threw through the notifier would be reported as the write failing. For
/// a payment or a filed bank row that is the expensive mistake: the person records the same
/// money again.
/// </remarks>
public class NotifierFailureTest
{
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Flat = Guid.NewGuid();

    private readonly Mock<IUsersClient> _users = new();
    private readonly Mock<IInboxClient> _inbox = new();
    private readonly Mock<IBankConnectionsClient> _connections = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();

    private readonly SettleStateService _settling;
    private readonly InboxStateService _inboxState;
    private readonly BankCommands _bank;

    private readonly List<(string Message, Severity Severity)> _said = [];

    private readonly List<BankTransactionResponse> _rows = [Row("Lidl", 30m), Row("Blue Bottle", 4.5m)];

    private bool _planDown;
    private bool _inboxDown;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiException Unavailable() =>
        new("down", 503, "", new Dictionary<string, IEnumerable<string>>(), null);

    public NotifierFailureTest()
    {
        _snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        _users
            .Setup(client => client.GetSettlementPlanAsync(It.IsAny<DateOnly?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _planDown
                ? throw Unavailable()
                : new SettlementPlanResponse(-40m, 0m, 40m,
                    [new PersonSettlement(Omar, "Omar", 40m, [new GroupDebt(Flat, "The flat", 40m)])], [], null));

        _users
            .Setup(client => client.SettleWithPersonAsync(It.IsAny<SettleWithPersonRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SettleWithPersonRequest request, CancellationToken _) =>
                new SettleWithPersonResponse(request.UserId, "Omar", request.Amount, request.Direction,
                    DateTimeOffset.UtcNow, null, [new GroupDebt(Flat, "The flat", request.Amount)]));

        _inbox
            .Setup(client => client.GetInboxSummaryAsync(It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _inboxDown
                ? throw Unavailable()
                : new InboxSummaryResponse(_rows.Count(row => row.Status == InboxStatus.New)));

        _inbox
            .Setup(client => client.FileBankTransactionAsync(It.IsAny<Guid>(),
                It.IsAny<FileBankTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, FileBankTransactionRequest request, CancellationToken _) =>
            {
                var row = _rows.Single(candidate => candidate.Id == id);
                _rows[_rows.IndexOf(row)] = row with { Status = InboxStatus.Filed };

                return new TransactionResponse
                {
                    Id = Guid.NewGuid(), Name = row.Title, Amount = row.Amount, GroupId = request.GroupId
                };
            });

        _connections
            .Setup(client => client.GetBankConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BankConnectionsResponse(true, []));

        var presenter = new ApiErrorPresenter(new Mock<NavigationManager>().Object, _snackbar.Object);
        var guard = new LoadGuard(presenter);

        _settling = new SettleStateService(_users.Object, new SettleTracker(), guard, _changes, presenter,
            _snackbar.Object);

        _inboxState = new InboxStateService(_inbox.Object, _connections.Object, guard, _changes,
            new LocalClock(Mock.Of<IJSRuntime>()));

        _bank = new BankCommands(_connections.Object, _inbox.Object, presenter, _snackbar.Object, _changes);
    }

    private Task<SettleWithPersonResponse?> PayOmarAsync() =>
        _settling.SettleAsync(new SettleWithPersonRequest
        {
            UserId = Omar,
            Amount = 40m,
            Direction = SettlementDirection.YouPaidThem,
            Date = DateTimeOffset.UtcNow
        }, "Omar", Ct);

    [Fact]
    public async Task A_payment_whose_plan_cannot_be_re_read_is_still_reported_as_recorded()
    {
        await _settling.EnsureLoadedAsync(Ct);
        _planDown = true;

        var recorded = await PayOmarAsync();

        Assert.NotNull(recorded);
        Assert.Contains(_said, said => said.Severity == Severity.Success
                                       && said.Message.StartsWith("Recorded ", StringComparison.Ordinal));
        Assert.Contains(_said, said => said.Severity == Severity.Error
                                       && said.Message.StartsWith("Could not load your settlement plan.",
                                           StringComparison.Ordinal));
        Assert.DoesNotContain(_said, said => said.Message.StartsWith("Could not record", StringComparison.Ordinal));

        // The figures already on the page stay rather than emptying into "All settled".
        Assert.Equal(40m, _settling.Plan!.YouOweTotal);
    }

    [Fact]
    public async Task A_filed_row_whose_inbox_cannot_be_re_read_is_still_reported_as_filed()
    {
        await _inboxState.EnsureLoadedAsync(Ct);
        _inboxDown = true;

        var expense = await _bank.FileAsync(_rows[0].Id, new FileBankTransactionRequest { GroupId = Flat },
            "The flat", Ct);

        Assert.NotNull(expense);
        Assert.Contains(_said, said => said is { Message: "Lidl added to The flat.", Severity: Severity.Success });
        Assert.DoesNotContain(_said, said => said.Message.StartsWith("Could not add", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every state hears every announcement, so one that fails its re-read must not cost the
    /// others theirs: the inbox badge still drops when the settle plan is the one that fails.
    /// </summary>
    [Fact]
    public async Task One_state_failing_its_re_read_does_not_keep_the_others_from_theirs()
    {
        await _settling.EnsureLoadedAsync(Ct);
        await _inboxState.EnsureLoadedAsync(Ct);
        Assert.Equal(2, _inboxState.NewCount);

        _planDown = true;

        var expense = await _bank.FileAsync(_rows[0].Id, new FileBankTransactionRequest { GroupId = Flat },
            "The flat", Ct);

        Assert.NotNull(expense);
        Assert.Equal(1, _inboxState.NewCount);
        Assert.Contains(_said, said => said.Message.StartsWith("Could not load your settlement plan.",
            StringComparison.Ordinal));
    }

    private static BankTransactionResponse Row(string merchant, decimal amount) =>
        new(Guid.NewGuid(), new DateOnly(2026, 9, 1), amount, "USD", merchant.ToUpperInvariant(), merchant,
            null, null, null, null, null, null, null, false, InboxStatus.New, null, null, "Everyday", "Fake Bank");
}

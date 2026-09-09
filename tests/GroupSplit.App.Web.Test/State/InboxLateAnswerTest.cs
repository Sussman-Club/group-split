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
/// What the inbox shows when two reads are in flight and the older one answers last.
/// </summary>
/// <remarks>
/// A write announces itself and the inbox re-reads; somebody switches to the Added tab
/// while that read is still out; the Added rows arrive, and then the earlier read's
/// Waiting rows arrive after them. The state used to write down whatever arrived last,
/// which put the waiting rows under a tab that says Added. Same defect as the expenses
/// page's <see cref="LateAnswerTest"/>, same guard.
/// </remarks>
public class InboxLateAnswerTest
{
    private readonly TaskCompletionSource _waitingAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether reads of the waiting rows are held open. Off for the first read.</summary>
    private bool _waitingIsSlow;

    private readonly Mock<IInboxClient> _inbox = new();
    private readonly Mock<IBankConnectionsClient> _connections = new();
    private readonly InboxStateService _state;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public InboxLateAnswerTest()
    {
        _inbox
            .Setup(client => client.GetInboxSummaryAsync(It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InboxSummaryResponse(1));

        _inbox
            .Setup(client => client.GetInboxAsync(It.IsAny<InboxStatus?>(), It.IsAny<DateOnly?>(),
                It.IsAny<DateOnly?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (InboxStatus? status, DateOnly? _, DateOnly? _, string _, bool? _, int? _, int? _,
                CancellationToken _) =>
            {
                if (_waitingIsSlow && status == InboxStatus.New)
                    await _waitingAnswers.Task;

                var row = Row(status == InboxStatus.Filed ? "Filed row" : "Waiting row", status ?? InboxStatus.New);

                return new PagedResponseOfBankTransactionResponse([row], 1, 25, 1);
            });

        _connections
            .Setup(client => client.GetBankConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BankConnectionsResponse(true, []));

        var presenter = new ApiErrorPresenter(Mock.Of<IAuthService>(), new Mock<NavigationManager>().Object,
            Mock.Of<ISnackbar>());

        _state = new InboxStateService(_inbox.Object, _connections.Object, new LoadGuard(presenter),
            new DataChangeNotifier(), new LocalClock(Mock.Of<IJSRuntime>()));
    }

    [Fact]
    public async Task The_rows_of_the_filter_that_was_left_do_not_land_under_the_one_that_is_open()
    {
        await _state.LoadRowsAsync(Ct);
        Assert.Equal("Waiting row", Assert.Single(_state.Rows).Title);

        _waitingIsSlow = true;

        // A write announced itself: the waiting rows are re-read, and do not answer yet.
        var refresh = _state.RefreshAsync(Ct);

        // Meanwhile the Added tab is opened, and answers straight away.
        await _state.SetFilterAsync(InboxStatus.Filed, Ct);

        Assert.Equal(InboxStatus.Filed, _state.Filter);
        Assert.Equal("Filed row", Assert.Single(_state.Rows).Title);

        // Now the waiting rows answer. Nobody is on that tab.
        _waitingAnswers.SetResult();
        await refresh;

        Assert.Equal(InboxStatus.Filed, _state.Filter);
        Assert.Equal("Filed row", Assert.Single(_state.Rows).Title);
    }

    private static BankTransactionResponse Row(string title, InboxStatus status) =>
        new(Guid.NewGuid(), new DateOnly(2026, 9, 1), 30m, "USD", title.ToUpperInvariant(), title,
            null, null, null, null, null, null, null, false, status, null, null,
            "Everyday", "Fake Bank");
}

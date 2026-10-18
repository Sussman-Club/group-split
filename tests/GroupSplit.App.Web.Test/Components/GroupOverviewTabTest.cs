using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's Balances tab, and when it draws what the state service holds.
/// </summary>
/// <remarks>
/// The tab reads the balances from the state service rather than being handed them, and
/// reads its recent entries itself. Two answers, in either order. When the entries came
/// first, the tab drew with no balances and never drew again: the balances landing
/// re-rendered the page above it, but none of the tab's parameters had changed, so Blazor
/// left it alone. Seen after claiming an invitation -- "Balances appear once there is an
/// expense." under a list of expenses -- and fixed only by a reload.
/// </remarks>
public class GroupOverviewTabTest : ComponentTest
{
    private static readonly GroupResponse Group = new(Guid.NewGuid(), "Weekend in Lisbon", 2);

    private readonly Mock<IGroupsPageStateService> _state = new();
    private readonly Mock<IGroupsClient> _groups = new();

    /// <summary>What the state service holds. Empty until the test lands it.</summary>
    private UserGroupBalanceResponse? _balance;

    public GroupOverviewTabTest()
    {
        _state.SetupGet(state => state.SelectedGroup).Returns(Group);
        _state.SetupGet(state => state.Balance).Returns(() => _balance);

        _groups
            .Setup(client => client.GetGroupActivityAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool?>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfGroupLedgerEntryResponse([], 1, 3, 0));

        // A real MudBlazor reference, already finished with a yes -- the state a component is
        // actually handed. See ConfirmationResultTest for why a stand-in is the wrong tool.
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<ConfirmationDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ConfirmationDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);

                reference.Dismiss(DialogResult.Ok(true));

                return reference;
            });

        Services.AddSingleton(_state.Object);
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_dialogs.Object);
    }

    private readonly Mock<IDialogService> _dialogs = new();

    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Dani = Guid.NewGuid();
    private static readonly Guid Luz = Guid.NewGuid();

    private IRenderedComponent<GroupOverviewTab> RenderFor(Guid userId) =>
        Render<GroupOverviewTab>(parameters => parameters
            .Add(component => component.GroupId, Group.Id)
            .Add(component => component.GroupName, Group.Name)
            .Add(component => component.UserId, userId));

    /// <summary>
    /// A guest who owes the reader is one of the people they are owed by, counted with the
    /// members, and their row says they are a guest -- with a button that says whose payment
    /// the reader is recording, since the guest cannot record it themselves.
    /// </summary>
    [Fact]
    public void A_guest_who_owes_you_is_counted_and_named_as_a_guest()
    {
        _balance = new UserGroupBalanceResponse
        {
            NetBalances =
            [
                new GroupNetBalance { UserId = Me, UserName = "Ana Ruiz", Balance = 30m },
                new GroupNetBalance { UserId = Omar, UserName = "Omar Rivero", Balance = -10m },
                new GroupNetBalance { UserId = Dani, UserName = "Dani", Balance = -20m, IsGuest = true }
            ],
            OwedToYou =
            [
                new DebtInfo { UserId = Omar, UserName = "Omar Rivero", Amount = 10m },
                new DebtInfo { UserId = Dani, UserName = "Dani", Amount = 20m }
            ]
        };

        var tab = RenderFor(Me);

        tab.WaitForAssertion(() =>
            Assert.Contains("by 2 people in Weekend in Lisbon", tab.Markup, StringComparison.Ordinal));

        var rows = tab.FindAll(".gs-hero-row");

        var dani = rows.Single(row => row.TextContent.Contains("Dani", StringComparison.Ordinal));
        Assert.Equal("Dani (guest) owes you", dani.QuerySelector(".who")!.TextContent.Trim());
        Assert.Equal("Dani paid", dani.QuerySelector("button")!.TextContent.Trim());

        var omar = rows.Single(row => row.TextContent.Contains("Omar", StringComparison.Ordinal));
        Assert.Equal("They paid", omar.QuerySelector("button")!.TextContent.Trim());
    }

    /// <summary>
    /// A plan line between two guests is any member's to record, because nobody on either
    /// end can; a line between a guest and another member stays that member's call.
    /// </summary>
    [Fact]
    public async Task A_line_between_two_guests_can_be_recorded_by_any_member()
    {
        _balance = new UserGroupBalanceResponse
        {
            NetBalances =
            [
                new GroupNetBalance { UserId = Me, UserName = "Ana Ruiz", Balance = 0m },
                new GroupNetBalance { UserId = Omar, UserName = "Omar Rivero", Balance = 5m },
                new GroupNetBalance { UserId = Dani, UserName = "Dani", Balance = -20m, IsGuest = true },
                new GroupNetBalance { UserId = Luz, UserName = "Luz", Balance = 15m, IsGuest = true }
            ],
            Plan =
            [
                new SettlementPayment { FromUserId = Dani, FromUserName = "Dani", ToUserId = Luz, ToUserName = "Luz", Amount = 15m },
                new SettlementPayment { FromUserId = Dani, FromUserName = "Dani", ToUserId = Omar, ToUserName = "Omar Rivero", Amount = 5m }
            ]
        };

        var tab = RenderFor(Me);

        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll(".gs-plan-row").Count));

        var lines = tab.FindAll(".gs-plan-row");

        // Two guests: a button, named for both of them.
        var betweenGuests = lines.Single(line => line.TextContent.Contains("Luz", StringComparison.Ordinal));
        var record = betweenGuests.QuerySelector("button")!;
        Assert.Equal("Dani paid Luz", record.TextContent.Trim());

        // A guest and another member: that member's call, as between two other members.
        var withOmar = lines.Single(line => line.TextContent.Contains("Omar", StringComparison.Ordinal));
        Assert.Null(withOmar.QuerySelector("button"));
        Assert.Contains("their call", withOmar.TextContent, StringComparison.Ordinal);

        await record.ClickAsync(new());

        _state.Verify(state => state.RecordRepaymentAsync(Group.Id,
            It.Is<RecordRepaymentRequest>(request =>
                request.FromUserId == Dani && request.ToUserId == Luz && request.Amount == 15m),
            "Dani", "Luz", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The People panel's guest mark is separated from the name. Razor drops a literal space
    /// that only precedes code, and it read "Dani(guest)".
    /// </summary>
    [Fact]
    public void A_guest_in_the_people_panel_is_marked_with_a_space_before_the_mark()
    {
        _balance = new UserGroupBalanceResponse
        {
            NetBalances =
            [
                new GroupNetBalance { UserId = Me, UserName = "Ana Ruiz", Balance = 20m },
                new GroupNetBalance { UserId = Dani, UserName = "Dani", Balance = -20m, IsGuest = true }
            ]
        };

        var tab = RenderFor(Me);

        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll(".gs-balance-row").Count));

        var dani = tab.FindAll(".gs-balance-row .name")
            .Single(name => name.TextContent.Contains("Dani", StringComparison.Ordinal));

        Assert.Contains("Dani (guest)", System.Text.RegularExpressions.Regex.Replace(dani.TextContent, @"\s+", " "),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan line the reader pays on starts "You pay", not "you pays".
    /// </summary>
    [Fact]
    public void A_plan_line_you_pay_on_reads_you_pay()
    {
        _balance = new UserGroupBalanceResponse
        {
            NetBalances =
            [
                new GroupNetBalance { UserId = Me, UserName = "Ana Ruiz", Balance = -10m },
                new GroupNetBalance { UserId = Omar, UserName = "Omar Rivero", Balance = 10m }
            ],
            YouOwed = [new DebtInfo { UserId = Omar, UserName = "Omar Rivero", Amount = 10m }],
            Plan =
            [
                new SettlementPayment { FromUserId = Me, FromUserName = "Ana Ruiz", ToUserId = Omar, ToUserName = "Omar Rivero", Amount = 10m }
            ]
        };

        var tab = RenderFor(Me);

        tab.WaitForAssertion(() => Assert.Single(tab.FindAll(".gs-plan-row")));

        var who = System.Text.RegularExpressions.Regex.Replace(
            tab.Find(".gs-plan-row .who").TextContent, @"\s+", " ").Trim();

        Assert.Equal("You pay Omar Rivero", who);
    }

    /// <summary>
    /// The reader owes Omar 10 and Dani 15 directly, and Dani owes Omar 15, so the plan
    /// folds all three into one payment of 25 to Omar. The hero lists the debts and the
    /// plan lists the payment, and the two figures differ on purpose.
    /// </summary>
    private void YouOweOmarThroughDani() =>
        _balance = new UserGroupBalanceResponse
        {
            NetBalances =
            [
                new GroupNetBalance { UserId = Me, UserName = "Ana Ruiz", Balance = -25m },
                new GroupNetBalance { UserId = Omar, UserName = "Omar Rivero", Balance = 25m },
                new GroupNetBalance { UserId = Dani, UserName = "Dani", Balance = 0m }
            ],
            YouOwed =
            [
                new DebtInfo { UserId = Omar, UserName = "Omar Rivero", Amount = 10m },
                new DebtInfo { UserId = Dani, UserName = "Dani", Amount = 15m }
            ],
            Plan =
            [
                new SettlementPayment { FromUserId = Me, FromUserName = "Ana Ruiz", ToUserId = Omar, ToUserName = "Omar Rivero", Amount = 25m }
            ]
        };

    /// <summary>
    /// The tab, with its settle callback doing what <c>GroupDetail</c> does once its dialog
    /// has been confirmed at the figure it opened with: settling through the group commands,
    /// over this test's groups client.
    /// </summary>
    private IRenderedComponent<GroupOverviewTab> RenderWithThePagesWrite()
    {
        var commands = new GroupCommands(_groups.Object, Mock.Of<IInvitationsClient>(),
            Services.GetRequiredService<ApiErrorPresenter>(), Snackbar.Object, Changes);

        return Render<GroupOverviewTab>(parameters => parameters
            .Add(component => component.GroupId, Group.Id)
            .Add(component => component.GroupName, Group.Name)
            .Add(component => component.UserId, Me)
            .Add(component => component.OnSettle, ((DebtInfo Debt, SettlementDirection Direction) line) =>
                commands.SettleAsync(Group.Id, new SettleRequest
                {
                    UserId = line.Debt.UserId,
                    Amount = line.Debt.Amount,
                    Direction = line.Direction
                }, line.Debt.UserName)));
    }

    private void VerifyRecentEntriesRead(Times times) =>
        _groups.Verify(client => client.GetGroupActivityAsync(
            Group.Id, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int?>(),
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool?>(), It.IsAny<Guid?>(), It.IsAny<string>(),
            It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), times);

    /// <summary>
    /// "I paid them" on the reader's plan line records the plan's figure, as a payment the
    /// reader made to the person on the other end. The write is announced, so the recent
    /// entries under the plan are read again to show it.
    /// </summary>
    [Fact]
    public async Task I_paid_them_on_a_plan_line_records_the_plans_figure_as_your_payment()
    {
        YouOweOmarThroughDani();

        var tab = RenderWithThePagesWrite();

        tab.WaitForAssertion(() => Assert.Single(tab.FindAll(".gs-plan-row")));

        var record = tab.Find(".gs-plan-row button");
        Assert.Equal("I paid them", record.TextContent.Trim());

        await record.ClickAsync(new());

        // The plan's 25, not the 10 owed to Omar directly: paying 10 would leave the reader
        // owing Dani, who owes Omar, and the plan exists so that does not take three payments.
        _groups.Verify(client => client.SettleGroupDebtsAsync(Group.Id,
            It.Is<SettleRequest>(request =>
                request.UserId == Omar && request.Amount == 25m &&
                request.Direction == SettlementDirection.YouPaidThem),
            It.IsAny<CancellationToken>()), Times.Once);

        Snackbar.Verify(bar => bar.Add("Recorded: you paid Omar Rivero $25.00.", Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);

        // The tab's own write is for lines between two guests, and this is not one.
        _state.Verify(state => state.RecordRepaymentAsync(It.IsAny<Guid>(), It.IsAny<RecordRepaymentRequest>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        VerifyRecentEntriesRead(Times.Exactly(2));
    }

    /// <summary>
    /// A refused recording says what did not happen and announces nothing, so the line
    /// stays as it was, at the same figure, for the reader to try again.
    /// </summary>
    [Fact]
    public async Task A_refused_I_paid_them_says_so_and_leaves_the_line()
    {
        YouOweOmarThroughDani();

        _groups
            .Setup(client => client.SettleGroupDebtsAsync(It.IsAny<Guid>(), It.IsAny<SettleRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.SettlementNothingToSettle));

        var tab = RenderWithThePagesWrite();

        tab.WaitForAssertion(() => Assert.Single(tab.FindAll(".gs-plan-row")));

        await tab.Find(".gs-plan-row button").ClickAsync(new());

        Snackbar.Verify(bar => bar.Add(
            It.Is<string>(message => message.StartsWith("Could not record the settlement.", StringComparison.Ordinal)),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
        Snackbar.Verify(bar => bar.Add(It.IsAny<string>(), Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);

        VerifyRecentEntriesRead(Times.Once());

        var line = Assert.Single(tab.FindAll(".gs-plan-row"));
        Assert.Equal("You pay Omar Rivero",
            System.Text.RegularExpressions.Regex.Replace(line.QuerySelector(".who")!.TextContent, @"\s+", " ").Trim());
        Assert.Equal("I paid them", line.QuerySelector("button")!.TextContent.Trim());
    }

    [Fact]
    public void Balances_that_land_after_the_recent_entries_are_drawn()
    {
        var tab = Render<GroupOverviewTab>(parameters => parameters
            .Add(component => component.GroupId, Group.Id)
            .Add(component => component.GroupName, Group.Name));

        // The entries have answered and the balances have not: nothing to show yet.
        tab.WaitForAssertion(() =>
            Assert.Contains("Balances appear once there is an expense.", tab.Markup, StringComparison.Ordinal));

        // Then they land, which is announced by the state service and by nothing the tab
        // is handed.
        _balance = new UserGroupBalanceResponse
        {
            NetBalances = [new GroupNetBalance { UserId = Guid.NewGuid(), UserName = "Omar Rivero", Balance = 12m }]
        };

        _state.Raise(state => state.OnTransactionsChanged += null);

        tab.WaitForAssertion(() =>
        {
            Assert.Contains("Omar Rivero", tab.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Balances appear once there is an expense.", tab.Markup, StringComparison.Ordinal);
        });
    }
}

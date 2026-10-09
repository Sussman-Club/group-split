using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
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

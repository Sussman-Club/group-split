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

        Services.AddSingleton(_state.Object);
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(Mock.Of<IDialogService>());
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

using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The Expenses tab as it actually runs: inside the group page, which owns it, keys it and
/// re-renders it on three separate announcements of its own.
/// </summary>
/// <remarks>
/// <see cref="GroupExpensesTabTest"/> renders the tab on its own, which is the right shape
/// for asking what the tab does with an answer. It cannot see anything the parent does to
/// it, and the parent does a great deal: it re-renders the tab whenever the group's state
/// says anything, which is what made #174 possible in the first place. These drive the
/// filter through the real page.
/// </remarks>
public class GroupDetailExpensesTabTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly GroupResponse Group = new(GroupId, "Weekend in Lisbon", 3);

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<IGroupsPageStateService> _state = new();

    private readonly List<(DateTimeOffset? From, DateTimeOffset? To)> _asks = [];

    public GroupDetailExpensesTabTest()
    {
        _groups
            .Setup(client => client.GetGroupTransactionsSummaryAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string _, bool? _,
                string _, CancellationToken _) =>
            {
                _asks.Add((from, to));

                // All time is five expenses of 29.00; any span at all holds none of them.
                return from is null && to is null
                    ? new TransactionSummaryResponse(5, 29m)
                    : new TransactionSummaryResponse(0, 0m);
            });

        _groups
            .Setup(client => client.GetGroupTransactionsAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfTransactionResponse([], 1, 25, 0));

        _state.SetupGet(state => state.Groups).Returns([Group]);
        _state.SetupGet(state => state.IsReadyTask).Returns(Task.CompletedTask);
        _state.SetupGet(state => state.IsLoading).Returns(false);
        _state.SetupProperty(state => state.SelectedGroup, Group);
        _state.SetupGet(state => state.Transactions)
            .Returns(new PagedResponseOfTransactionResponse([], 1, 25, 5));
        _state.SetupGet(state => state.Balance).Returns(new UserGroupBalanceResponse { NetBalances = [] });

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_state.Object);
        Services.AddSingleton(Mock.Of<ITransactionsPageStateService>());
    }

    private IRenderedComponent<GroupDetail> RenderTab() =>
        Render<GroupDetail>(parameters => parameters
            .Add(page => page.GroupId, GroupId)
            .Add(page => page.Tab, "expenses"));

    private static (string Count, string Total) Cards(IRenderedComponent<GroupDetail> page)
    {
        var values = page.FindAll(".gs-stat-value");

        return (values[0].TextContent.Trim(), values[1].TextContent.Trim());
    }

    [Fact]
    public void The_tab_opens_on_the_whole_group()
    {
        var page = RenderTab();

        Assert.Equal(("5", "$29.00"), Cards(page));
    }

    /// <summary>
    /// The reported defect, driven through the page that owns the tab rather than the tab
    /// alone: pick a span with nothing in it, and both cards have to follow the list down.
    /// </summary>
    [Fact]
    public async Task Picking_a_span_moves_both_cards_when_the_tab_is_inside_its_page()
    {
        var page = RenderTab();

        await page.FindAll(".gs-chip").First(chip => chip.TextContent.Contains("Last month"))
            .ClickAsync(new());

        Assert.NotNull(_asks.Last().From);
        Assert.Equal(("0", "$0.00"), Cards(page));
    }

    /// <summary>
    /// And the cards survive the parent re-rendering the tab afterwards, which the group
    /// page does on every announcement its own state makes.
    /// </summary>
    [Fact]
    public async Task The_narrowed_figures_survive_the_page_re_rendering_the_tab()
    {
        var page = RenderTab();

        await page.FindAll(".gs-chip").First(chip => chip.TextContent.Contains("Last month"))
            .ClickAsync(new());

        Assert.Equal(("0", "$0.00"), Cards(page));

        // Each of the three the page subscribes to, one after another.
        _state.Raise(state => state.OnTransactionsChanged += null);
        _state.Raise(state => state.OnGroupsChanged += null);
        _state.Raise(state => state.OnGroupSelected += null);

        page.Render();

        Assert.Equal(("0", "$0.00"), Cards(page));
    }
}

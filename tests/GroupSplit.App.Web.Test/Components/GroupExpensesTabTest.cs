using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's Expenses tab: two summary cards over a list, both narrowed by the same
/// chips. What is pinned here is that the cards and the list can never be describing
/// different questions, which is the defect they were written against -- picking a range
/// emptied the list and left the cards showing the all-time count and total, under a
/// caption naming the range.
/// </summary>
public class GroupExpensesTabTest : ComponentTest
{
    private static readonly Guid Group = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();

    /// <summary>Every summary request the component made, in order, as it asked it.</summary>
    private readonly List<Ask> _asks = [];

    /// <summary>Answers a summary request. Set per test to control what comes back and when.</summary>
    private Func<Ask, Task<TransactionSummaryResponse>> _answer =
        _ => Task.FromResult(new TransactionSummaryResponse(5, 29m));

    private record Ask(DateTimeOffset? From, DateTimeOffset? To, string? Search)
    {
        public bool IsAllTime => From is null && To is null;
    }

    public GroupExpensesTabTest()
    {
        _groups
            .Setup(client => client.GetGroupTransactionsSummaryAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid _, DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string _, bool? _,
                string search, CancellationToken _) =>
            {
                var ask = new Ask(from, to, search);
                _asks.Add(ask);

                return _answer(ask);
            });

        // The grid under the cards reads its own rows. It is not what these are about, so
        // it answers an empty page and is otherwise left alone.
        _groups
            .Setup(client => client.GetGroupTransactionsAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfTransactionResponse([], 1, 25, 0));

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(Mock.Of<ITransactionsPageStateService>());
    }

    private IRenderedComponent<GroupExpensesTab> Render() =>
        base.Render<GroupExpensesTab>(parameters => parameters.Add(tab => tab.GroupId, Group));

    /// <summary>The big number on the first card, and the money on the second.</summary>
    private static (string Count, string Total) Cards(IRenderedComponent<GroupExpensesTab> tab)
    {
        var values = tab.FindAll(".gs-stat-value");

        return (values[0].TextContent.Trim(), values[1].TextContent.Trim());
    }

    private static Task PickLastMonthAsync(IRenderedComponent<GroupExpensesTab> tab) =>
        tab.FindAll(".gs-chip").First(chip => chip.TextContent.Contains("Last month")).ClickAsync(new());

    [Fact]
    public void The_cards_start_on_the_whole_group()
    {
        var tab = Render();

        Assert.Equal(("5", "$29.00"), Cards(tab));
        Assert.True(Assert.Single(_asks).IsAllTime);
    }

    [Fact]
    public async Task Picking_a_range_asks_about_that_range_and_shows_what_comes_back()
    {
        _answer = ask => Task.FromResult(ask.IsAllTime
            ? new TransactionSummaryResponse(5, 29m)
            : new TransactionSummaryResponse(0, 0m));

        var tab = Render();

        await PickLastMonthAsync(tab);

        var narrowed = _asks.Last();

        Assert.NotNull(narrowed.From);
        Assert.NotNull(narrowed.To);

        // The whole point of #174: nothing on the cards still describes all time.
        Assert.Equal(("0", "$0.00"), Cards(tab));
        Assert.Contains("last month", tab.Markup);
    }

    /// <summary>
    /// The defect itself, as a race rather than as a wrong request. Every request the tab
    /// made was correct; the group page re-renders it on each of three announcements about
    /// the group, each render asked again, and an all-time answer from before the chip was
    /// clicked could land after the filtered one -- putting the all-time figures back on
    /// cards captioned with the range, where nothing would read them again.
    /// </summary>
    [Fact]
    public async Task An_all_time_answer_that_arrives_late_does_not_land_on_the_cards()
    {
        var allTimeAnswered = new TaskCompletionSource<TransactionSummaryResponse>();

        _answer = ask => ask.IsAllTime
            ? allTimeAnswered.Task
            : Task.FromResult(new TransactionSummaryResponse(0, 0m));

        var tab = Render();

        // The first read is still in flight, the way a cold start leaves it. The range is
        // picked while it is, which is the ordinary way to use the page.
        await PickLastMonthAsync(tab);

        Assert.Equal(("0", "$0.00"), Cards(tab));

        // Now the stale one answers. The component resumes on the renderer's dispatcher, so
        // it is given its turns there before anything is looked at -- asserting straight
        // after the result is set would pass by having looked too early to see the damage.
        allTimeAnswered.SetResult(new TransactionSummaryResponse(5, 29m));

        for (var turn = 0; turn < 5; turn++)
            await tab.InvokeAsync(() => { });

        Assert.Equal(("0", "$0.00"), Cards(tab));
    }

    /// <summary>
    /// The group page subscribes to three of its own state's announcements and re-renders
    /// this tab on each. A request per render is what opened the window above, so a render
    /// that changes no part of the question asks nothing.
    /// </summary>
    [Fact]
    public void Rendering_again_without_a_new_question_asks_nothing_again()
    {
        var tab = Render();

        Assert.Single(_asks);

        tab.Render();
        tab.Render(parameters => parameters.Add(component => component.GroupId, Group));

        Assert.Single(_asks);
    }

    /// <summary>
    /// The counterpart: the narrowing has not moved, but the figures under it have, and the
    /// cards cannot notice that on their own. They take the same announcements the grid
    /// under them reloads on.
    /// </summary>
    [Fact]
    public async Task A_write_announced_anywhere_reads_the_cards_again()
    {
        var tab = Render();

        Assert.Single(_asks);

        _answer = _ => Task.FromResult(new TransactionSummaryResponse(6, 41.50m));

        await Changes.NotifyTransactionsChangedAsync();

        tab.Render();

        Assert.Equal(2, _asks.Count);
        Assert.True(_asks.Last().IsAllTime);
        Assert.Equal(("6", "$41.50"), Cards(tab));
    }

    /// <summary>
    /// The search box narrows the list, so it narrows the figures beside it too. A total
    /// over everything, beside eight matching rows, is the same disagreement as the one
    /// the range chips had.
    /// </summary>
    [Fact]
    public async Task Searching_narrows_the_cards_as_well_as_the_list()
    {
        _answer = ask => Task.FromResult(ask.Search is null
            ? new TransactionSummaryResponse(5, 29m)
            : new TransactionSummaryResponse(1, 4.50m));

        var tab = Render();

        var search = tab.Find(".gs-search input");
        await search.InputAsync(new() { Value = "coffee" });

        // The box is debounced, so the request follows the pause rather than the keystroke.
        tab.WaitForAssertion(() => Assert.Equal("coffee", _asks.Last().Search), TimeSpan.FromSeconds(5));

        tab.WaitForAssertion(() => Assert.Equal(("1", "$4.50"), Cards(tab)), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A refusal is shown once and leaves the cards reading zero rather than the figures
    /// for a question nobody asked.
    /// </summary>
    [Fact]
    public void A_refused_read_says_so_instead_of_showing_the_wrong_figures()
    {
        _answer = _ => throw new HttpRequestException("the server could not be reached");

        var tab = Render();

        Assert.Equal(("0", "$0.00"), Cards(tab));

        Snackbar.Verify(
            bar => bar.Add(It.Is<string>(message => message.Contains("Could not load this group's expenses.")),
                Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }
}

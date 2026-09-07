using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The three cards over the personal expenses list, and whether they describe the same
/// question the list under them is answering.
/// </summary>
/// <remarks>
/// The group's Expenses tab had this defect as issue #174 and it was fixed there; this page
/// had the same shape and kept it. Its Total card followed the narrowing and its Recorded
/// card did not, so picking a span moved one figure and left the other reading the whole
/// ledger -- under a subline naming the span.
/// <para>
/// The state service is mocked rather than real: what is under test is which figure each
/// card reads, and the service's own reads have their own tests in
/// <see cref="State.PageStateRefreshTest"/>.
/// </para>
/// </remarks>
public class TransactionsPageTest : ComponentTest
{
    private readonly Mock<ITransactionsPageStateService> _state = new();

    public TransactionsPageTest()
    {
        // Everything the person has ever paid, and the much smaller slice a narrowing finds.
        _state.SetupGet(state => state.Summary).Returns(new TransactionSummaryResponse(5, 29m));
        _state.SetupGet(state => state.MatchesSummary).Returns(new TransactionSummaryResponse(1, 4.50m));
        _state.SetupGet(state => state.MonthSummary).Returns(new TransactionSummaryResponse(2, 12m));
        _state.SetupGet(state => state.Page)
            .Returns(new PagedResponseOfTransactionResponse([], 1, 25, 1));
        _state.SetupGet(state => state.Query).Returns(TransactionQuery.Default);
        _state.SetupGet(state => state.IsReadyTask).Returns(Task.CompletedTask);

        Services.AddSingleton(_state.Object);
    }

    private static (string Recorded, string Total) Cards(IRenderedComponent<Transactions> page)
    {
        var values = page.FindAll(".gs-stat-value");

        // Recorded, total. Two cards: "This month" was a third that did not follow the
        // filter and duplicated a chip in the date filter beside it.
        return (values[0].TextContent.Trim(), values[1].TextContent.Trim());
    }

    private static Task PickAsync(IRenderedComponent<Transactions> page, string label) =>
        page.FindAll(".gs-chip").First(chip => chip.TextContent.Contains(label)).ClickAsync(new());

    [Fact]
    public void Unnarrowed_the_cards_describe_the_whole_ledger()
    {
        var page = Render<Transactions>();

        Assert.Equal(("5", "$29.00"), Cards(page));
        Assert.Contains("all time", page.Markup);
    }

    /// <summary>
    /// The defect: the count kept describing everything while the total described the span.
    /// </summary>
    [Fact]
    public async Task Picking_a_span_moves_both_figures_and_not_just_the_total()
    {
        var page = Render<Transactions>();

        await PickAsync(page, "Last month");

        Assert.Equal(("1", "$4.50"), Cards(page));
    }

    /// <summary>
    /// The scope pills narrow too, and the page counts them as narrowing -- so the figures
    /// have to move for them as well, not only for the date chips.
    /// </summary>
    [Fact]
    public async Task Narrowing_by_scope_moves_them_too()
    {
        var page = Render<Transactions>();

        await page.FindAll("button").First(button => button.TextContent.Trim() == "Personal").ClickAsync(new());

        Assert.Equal(("1", "$4.50"), Cards(page));
    }

    /// <summary>
    /// Both cards carry the same subline, and it names the filters rather than counting
    /// anything -- the count is the figure directly above it. "6 in view" under a 6 was a
    /// subline that agreed with its own card and told nobody anything.
    /// </summary>
    [Fact]
    public async Task The_subline_says_which_expenses_the_figures_are_of()
    {
        var page = Render<Transactions>();

        await PickAsync(page, "Last month");
        await page.FindAll("button").First(button => button.TextContent.Trim() == "Personal").ClickAsync(new());

        var sublines = page.FindAll(".gs-stat .gs-muted").Select(line => line.TextContent.Trim()).ToList();

        Assert.Equal(2, sublines.Count);
        Assert.All(sublines, line => Assert.Equal("last month · personal", line));
        Assert.DoesNotContain("in view", page.Markup);
    }

    [Fact]
    public async Task Widening_back_puts_the_whole_ledger_back_on_the_cards()
    {
        var page = Render<Transactions>();

        await PickAsync(page, "Last month");
        await PickAsync(page, "All time");

        Assert.Equal(("5", "$29.00"), Cards(page));
    }
}

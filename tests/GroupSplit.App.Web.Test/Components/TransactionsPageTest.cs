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

        // Recorded, this month, total.
        return (values[0].TextContent.Trim(), values[2].TextContent.Trim());
    }

    private static Task PickAsync(IRenderedComponent<Transactions> page, string label) =>
        page.FindAll(".gs-chip").First(chip => chip.TextContent.Contains(label)).ClickAsync(new());

    [Fact]
    public void Unnarrowed_the_cards_describe_the_whole_ledger()
    {
        var page = Render<Transactions>();

        Assert.Equal(("5", "$29.00"), Cards(page));
        Assert.Contains("showing all", page.Markup);
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
    /// "This month" is its own question and says so on the card, so it stays put when the
    /// span moves. Pinned because the fix above could easily have been applied to all three.
    /// </summary>
    [Fact]
    public async Task This_month_is_not_narrowed_because_it_names_its_own_span()
    {
        var page = Render<Transactions>();

        await PickAsync(page, "Last month");

        Assert.Equal("$12.00", page.FindAll(".gs-stat-value")[1].TextContent.Trim());
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

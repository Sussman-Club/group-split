using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// You: your money across every group, in three named views.
/// </summary>
/// <remarks>
/// The page was called Expenses and showed one thing -- rows where you were the payer --
/// which is what made it read as a duplicate of a group's own listing. It has three views
/// now, they are the title, and they are in the URL.
/// <para>
/// The older defect it inherits is still pinned here: the two figures over the list must
/// describe the same question the list is answering. Its Total card used to follow the
/// narrowing while its count card did not, so picking a span moved one figure and left the
/// other reading the whole ledger, under a subline naming the span.
/// </para>
/// <para>
/// The figures come from the server, over the same filter as the listing, so the client is
/// mocked and what is asserted is which figure each card reads and what each is called.
/// </para>
/// </remarks>
public class TransactionsPageTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _client = new();
    private readonly Mock<IGroupsPageStateService> _groups = new();

    /// <summary>Every narrowing the page asked a summary about, in order.</summary>
    private readonly List<Ask> _asks = [];

    /// <summary>Whether the last share request asked for the owed rows only.</summary>
    private bool? _owedOnly;

    private record Ask(DateTimeOffset? From, DateTimeOffset? To, Guid? GroupId, bool? Personal, string? Search)
    {
        public bool IsAllTime => From is null && To is null;

        public bool IsWhole => IsAllTime && GroupId is null && Personal is null && Search is null;
    }

    public TransactionsPageTest()
    {
        // Everything the person has ever paid, and the much smaller slice a narrowing finds.
        _client
            .Setup(client => client.GetTransactionsSummaryAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? from, DateTimeOffset? to, Guid? group, Guid? _, string _,
                bool? personal, string search, CancellationToken _) =>
            {
                var ask = new Ask(from, to, group, personal, search);
                _asks.Add(ask);

                return ask.IsWhole
                    ? new TransactionSummaryResponse(5, 29m)
                    : new TransactionSummaryResponse(1, 4.50m);
            });

        _client
            .Setup(client => client.GetTransactionSharesSummaryAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? _, DateTimeOffset? _, Guid? _, Guid? _, string _, bool? _,
                string _, bool? owedOnly, CancellationToken _) =>
            {
                _owedOnly = owedOnly;

                // Count, what moved in all, the caller's part of it, and the part of that
                // which is actually a debt.
                return new ExpenseShareSummaryResponse(9, 120m, 40m, 31m);
            });

        _client
            .Setup(client => client.GetTransactionsAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfTransactionResponse([], 1, 10, 0));

        _client
            .Setup(client => client.GetTransactionSharesAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfExpenseShareResponse([], 1, 10, 0));

        _client
            .Setup(client => client.GetMonthlyExposureAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _groups.SetupGet(state => state.Groups).Returns([new GroupResponse(GroupId, "Weekend in Lisbon", 3)]);
        _groups.SetupGet(state => state.IsReadyTask).Returns(Task.CompletedTask);

        Services.AddSingleton(_client.Object);
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(Mock.Of<ITransactionsPageStateService>());
    }

    /// <summary>
    /// Renders the page at a URL rather than with a parameter: the view is read from the
    /// query string, and bUnit refuses to set a [SupplyParameterFromQuery] any other way --
    /// which is the right refusal, since navigating is what actually happens.
    /// </summary>
    private IRenderedComponent<Transactions> RenderView(string? view = null)
    {
        var nav = Services.GetRequiredService<NavigationManager>();

        nav.NavigateTo(view is null ? "/transactions" : $"/transactions?view={view}");

        return Render<Transactions>();
    }

    /// <summary>The big number on the first card, and the money on the second.</summary>
    private static (string Count, string Total) Cards(IRenderedComponent<Transactions> page)
    {
        var values = page.FindAll(".gs-stat-value");

        return (values[0].TextContent.Trim(), values[1].TextContent.Trim());
    }

    /// <summary>What the two cards are called, which changes with the view.</summary>
    private static (string Count, string Total) Labels(IRenderedComponent<Transactions> page)
    {
        var labels = page.FindAll(".gs-stat .gs-eyebrow");

        return (labels[0].TextContent.Trim(), labels[1].TextContent.Trim());
    }

    /// <summary>
    /// Sets the span, through the filter component's own callback -- the presets live in a
    /// Mud popover, outside the tree bUnit renders. See the note on the inbox's equivalent.
    /// </summary>
    private static Task PickSpanAsync(IRenderedComponent<Transactions> page, DateFilterPreset preset)
    {
        var filter = page.FindComponent<DateRangeFilter>();

        return page.InvokeAsync(() => filter.Instance.ValueChanged.InvokeAsync(new DateFilter(preset)));
    }

    /// <summary>
    /// Sets which ledger. Everything, shared, personal and one group are one control now,
    /// so there is no second one to contradict it. Its items are in a popover too, so the
    /// menu item is clicked by finding the handler it would have run.
    /// </summary>
    private static Task PickLedgerAsync(IRenderedComponent<Transactions> page, string where) =>
        page.InvokeAsync(() => page.Instance.SetLedger(where));

    // ---- the figures --------------------------------------------------------------------

    [Fact]
    public void Unnarrowed_the_cards_describe_the_whole_ledger()
    {
        var page = RenderView();

        Assert.Equal(("5", "$29.00"), Cards(page));
        Assert.Contains("all time", page.Markup);
    }

    /// <summary>
    /// The defect: the count kept describing everything while the total described the span.
    /// </summary>
    [Fact]
    public async Task Picking_a_span_moves_both_figures_and_not_just_the_total()
    {
        var page = RenderView();

        await PickSpanAsync(page, DateFilterPreset.LastMonth);

        Assert.Equal(("1", "$4.50"), Cards(page));
        Assert.NotNull(_asks.Last().From);
    }

    /// <summary>
    /// The scope pills narrow too, and the page counts them as narrowing -- so the figures
    /// have to move for them as well, not only for the date chips.
    /// </summary>
    [Fact]
    public async Task Narrowing_by_scope_moves_them_too()
    {
        var page = RenderView();

        await PickLedgerAsync(page, "personal");

        Assert.Equal(("1", "$4.50"), Cards(page));
        Assert.True(_asks.Last().Personal);
    }

    /// <summary>
    /// Both cards carry the same subline, and it names the filters rather than counting
    /// anything -- the count is the figure directly above it.
    /// </summary>
    [Fact]
    public async Task The_subline_says_which_expenses_the_figures_are_of()
    {
        var page = RenderView();

        await PickSpanAsync(page, DateFilterPreset.LastMonth);
        await PickLedgerAsync(page, "personal");

        var sublines = page.FindAll(".gs-stat .gs-muted").Select(line => line.TextContent.Trim()).ToList();

        Assert.Contains("last month · personal only", sublines);
        Assert.DoesNotContain("in view", page.Markup);
    }

    [Fact]
    public async Task Widening_back_puts_the_whole_ledger_back_on_the_cards()
    {
        var page = RenderView();

        await PickSpanAsync(page, DateFilterPreset.LastMonth);
        await PickSpanAsync(page, DateFilterPreset.AllTime);

        Assert.Equal(("5", "$29.00"), Cards(page));
    }

    // ---- the three views ----------------------------------------------------------------

    /// <summary>
    /// The view is the title. A chip under a heading called "Expenses" is what produced the
    /// confusion with a group's own listing in the first place.
    /// </summary>
    [Theory]
    [InlineData(null, "Paid by you")]
    [InlineData("share", "Your share")]
    [InlineData("everything", "Everything you are in")]
    public void The_view_in_the_url_is_the_heading(string? view, string expected)
    {
        var page = RenderView(view);

        Assert.Equal(expected, page.Find("h1").TextContent.Trim());
    }

    /// <summary>
    /// An unknown view falls back to the one the page has always shown rather than to an
    /// empty screen. A stale bookmark is not a reason to answer nothing.
    /// </summary>
    [Fact]
    public void An_unknown_view_falls_back_to_what_you_paid()
    {
        var page = RenderView("nonsense");

        Assert.Equal("Paid by you", page.Find("h1").TextContent.Trim());
    }

    /// <summary>
    /// The labels change with the view because the figures do. "Total paid" over a list of
    /// other people's expenses would be describing a set nobody asked about.
    /// </summary>
    [Theory]
    [InlineData(null, "Expenses", "Total paid")]
    [InlineData("share", "Expenses you are in", "Total owed")]
    [InlineData("everything", "Expenses you are in", "Your share")]
    public void Each_view_names_its_own_figures(string? view, string count, string total)
    {
        var page = RenderView(view);

        Assert.Equal((count, total), Labels(page));
    }

    /// <summary>
    /// Three views over two figures. Under "your share" the total is the part that is
    /// actually a debt; under "everything you are in" it is the whole of the reader's share,
    /// their own expenses included. A share of an expense you paid for yourself is money you
    /// already have.
    /// </summary>
    [Theory]
    [InlineData("share", "$31.00")]
    [InlineData("everything", "$40.00")]
    public void The_share_views_read_the_figure_their_own_question_asks_for(string view, string expected)
    {
        var page = RenderView(view);

        Assert.Equal(("9", expected), Cards(page));
    }

    /// <summary>
    /// Only one of the two share views is about debt, and the server is told which: the
    /// other one keeps the rows the reader paid for.
    /// </summary>
    [Theory]
    [InlineData("share", true)]
    [InlineData("everything", false)]
    public void Only_your_share_asks_the_server_for_the_owed_rows_alone(string view, bool expected)
    {
        RenderView(view);

        Assert.Equal(expected, _owedOnly);
    }

    /// <summary>
    /// The share column is absent where the reader paid all of it -- it would be the amount
    /// repeated beside itself -- and present in the two views where it is a different
    /// number.
    /// </summary>
    /// <remarks>
    /// Read off the grid's own headers rather than the page's markup: "Your share" is also
    /// the name of one of the three view chips, which is on screen whichever view is open.
    /// </remarks>
    [Theory]
    [InlineData(null, false)]
    [InlineData("share", true)]
    [InlineData("everything", true)]
    public void The_share_column_appears_only_where_it_says_something(string? view, bool expected)
    {
        var page = RenderView(view);

        var headers = page.FindAll(".gs-grid th").Select(cell => cell.TextContent.Trim()).ToList();

        Assert.Equal(expected, headers.Any(header => header.Contains("Your share")));
    }

    /// <summary>
    /// The group filter is an axis this page has and a group's own ledger cannot, and it
    /// starts wide: the subline says so, and the server is asked about no group in
    /// particular.
    /// </summary>
    [Fact]
    public void The_listing_spans_every_group_until_one_is_picked()
    {
        var page = RenderView();

        Assert.Contains("all groups", page.Markup);
        Assert.Null(_asks.Last().GroupId);
    }
}

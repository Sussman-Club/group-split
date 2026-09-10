using AngleSharp.Dom;
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
using MudBlazor;

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
    private readonly Mock<IDialogService> _dialogs = new();

    /// <summary>Every narrowing the page asked a summary about, in order.</summary>
    private readonly List<Ask> _asks = [];

    /// <summary>Whether the last share request asked for the owed rows only.</summary>
    private bool? _owedOnly;

    /// <summary>
    /// The share rows the grid is handed. Empty unless a test wants rows: most of these
    /// are about the figures over the list rather than the list.
    /// </summary>
    private List<ExpenseShareResponse> _rows = [];

    /// <summary>Which expense a details dialog was opened for, if one was.</summary>
    private Guid? _viewed;

    private record Ask(DateTimeOffset? From, DateTimeOffset? To, Guid? GroupId, bool? Personal, string? Search)
    {
        public bool IsAllTime => From is null && To is null;

        public bool IsWhole => Dimensions == 0;

        /// <summary>How many of the four filters are set.</summary>
        public int Dimensions =>
            (IsAllTime ? 0 : 1) + (GroupId is null ? 0 : 1)
            + (Personal is null ? 0 : 1) + (Search is null ? 0 : 1);
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

                // One figure per shape of narrowing, so a card reading the wrong ask reads
                // a visibly wrong number. Five for the whole ledger, one fewer per
                // dimension somebody has narrowed by.
                var count = 5 - ask.Dimensions;

                return new TransactionSummaryResponse(count, count * 5.5m);
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
            .ReturnsAsync(() => new PagedResponseOfExpenseShareResponse(_rows, 1, 10, _rows.Count));

        // A stand-in, so a test can see which expense the row asked to open. The real one
        // would render the dialog, which reads the expense over a client that has not been
        // told about it.
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<TransactionDetailsDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<TransactionDetailsDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters<TransactionDetailsDialog> parameters, DialogOptions _) =>
            {
                _viewed = parameters.Get<Guid>(nameof(TransactionDetailsDialog.TransactionId));

                return new DialogReference(Guid.NewGuid(), _dialogs.Object);
            });

        _client
            .Setup(client => client.GetMonthlyExposureAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _groups.SetupGet(state => state.Groups).Returns([new GroupResponse(GroupId, "Weekend in Lisbon", 3)]);
        _groups.SetupGet(state => state.IsReadyTask).Returns(Task.CompletedTask);

        Services.AddSingleton(_client.Object);
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_dialogs.Object);
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

    /// <summary>
    /// The page opens on the current month, and says so where the figures are.
    /// </summary>
    /// <remarks>
    /// It used to open on all time, which meant the two big figures were an all-time total
    /// nobody had asked for over a first page of whatever happened to be newest. A month is
    /// the span somebody reviews -- but a default narrowing that does not announce itself is
    /// the whole defect this session went after, so the subline names it.
    /// </remarks>
    [Fact]
    public void The_page_opens_on_the_current_month_and_the_cards_say_which()
    {
        var page = RenderView("paid");

        // One dimension narrowed: the span, and nothing else.
        Assert.Equal(("4", "$22.00"), Cards(page));
        Assert.Equal(1, _asks.Last().Dimensions);
        Assert.NotNull(_asks.Last().From);

        Assert.Contains("this month", page.Markup);
        Assert.DoesNotContain("all time", page.Markup);
    }

    /// <summary>
    /// The defect: the count kept describing everything while the total described the span.
    /// </summary>
    [Fact]
    public async Task Picking_a_span_re_asks_with_the_new_bounds()
    {
        var page = RenderView("paid");

        var opening = _asks.Last().From;

        await PickSpanAsync(page, DateFilterPreset.LastMonth);

        // Asserted on the bounds rather than the figures, because both spans narrow by the
        // same one dimension and would answer the same number. What has to be true is that
        // the page asked again, about the span now in force.
        Assert.NotEqual(opening, _asks.Last().From);
        Assert.Equal(1, _asks.Last().Dimensions);
        Assert.Contains("last month", page.Markup);
    }

    /// <summary>
    /// The scope pills narrow too, and the page counts them as narrowing -- so the figures
    /// have to move for them as well, not only for the date chips.
    /// </summary>
    [Fact]
    public async Task Narrowing_by_scope_moves_them_too()
    {
        var page = RenderView("paid");

        await PickLedgerAsync(page, "personal");

        // Two dimensions now: the month it opened on, and the ledger.
        Assert.Equal(("3", "$16.50"), Cards(page));
        Assert.True(_asks.Last().Personal);
    }

    /// <summary>
    /// Both cards carry the same subline, and it names the filters rather than counting
    /// anything -- the count is the figure directly above it.
    /// </summary>
    [Fact]
    public async Task The_subline_says_which_expenses_the_figures_are_of()
    {
        var page = RenderView("paid");

        await PickSpanAsync(page, DateFilterPreset.LastMonth);
        await PickLedgerAsync(page, "personal");

        var sublines = page.FindAll(".gs-stat .gs-muted").Select(line => line.TextContent.Trim()).ToList();

        Assert.Contains("last month · personal only", sublines);
        Assert.DoesNotContain("in view", page.Markup);
    }

    /// <summary>
    /// All time is still reachable, and reaching it puts the whole ledger on the cards.
    /// </summary>
    [Fact]
    public async Task Widening_to_all_time_puts_the_whole_ledger_on_the_cards()
    {
        var page = RenderView("paid");

        await PickSpanAsync(page, DateFilterPreset.LastMonth);
        await PickSpanAsync(page, DateFilterPreset.AllTime);

        Assert.Equal(("5", "$27.50"), Cards(page));
        Assert.True(_asks.Last().IsWhole);
    }

    // ---- saying which question is on screen ---------------------------------------------

    /// <summary>
    /// Each chip carries the line that says what it counts.
    /// </summary>
    /// <remarks>
    /// The two share views differ only by a flag: one is the reader's part of what other
    /// people paid, the other their part of everything, their own expenses included. Their
    /// labels were "Your share" and "Everything you are in", which name neither of those
    /// things, so the only way to tell which was on was to click one and watch the figures
    /// move. Pinned because a label is the cheapest thing in a page to quietly rewrite.
    /// </remarks>
    [Fact]
    public void Every_view_chip_says_what_it_counts()
    {
        var page = RenderView();

        var details = page.FindAll(".gs-chipbar .gs-chip-detail")
            .Select(chip => chip.TextContent.Trim())
            .ToList();

        Assert.Equal(
        [
            "expenses you covered",
            "your share of what others paid",
            "your share of every expense"
        ], details);
    }

    /// <summary>
    /// The eyebrow reports the ledger, and the heading reports the view, so each of the two
    /// controls has exactly one place on the page that speaks for it.
    /// </summary>
    /// <remarks>
    /// It read "Across every group" whatever the ledger control was set to, so a page
    /// narrowed to one group carried a line saying it was not -- next to a view chip called
    /// "Everything you are in" and a ledger menu whose default also read "Everything".
    /// </remarks>
    [Fact]
    public async Task The_eyebrow_follows_the_ledger_rather_than_always_saying_every_group()
    {
        var page = RenderView();

        Assert.Equal("Across every group", page.Find(".gs-eyebrow").TextContent.Trim());

        await PickLedgerAsync(page, GroupId.ToString());

        Assert.Equal("Weekend in Lisbon", page.Find(".gs-eyebrow").TextContent.Trim());

        await PickLedgerAsync(page, "personal");

        Assert.Equal("Personal expenses only", page.Find(".gs-eyebrow").TextContent.Trim());
    }

    /// <summary>
    /// The word "Everything" appeared in both controls, meaning two different things: a
    /// view over the reader's shares, and every ledger at once. Whichever one a reader had
    /// moved, the page looked the same.
    /// </summary>
    [Fact]
    public void The_two_controls_do_not_share_a_word()
    {
        var page = RenderView("everything");

        var chips = page.FindAll(".gs-chipbar .gs-chip-label").Select(chip => chip.TextContent.Trim());

        // By its own label, not by class: the date filter is a menu chip too, and it was
        // the one this found.
        var ledger = page.Find("[aria-label^='Which ledger']").QuerySelector(".label")!.TextContent.Trim();

        Assert.Equal("All groups", ledger);
        Assert.DoesNotContain(ledger, chips);
    }

    /// <summary>
    /// The chart is gone, and stays gone.
    /// </summary>
    /// <remarks>
    /// Two lines -- what the reader paid and what their share came to -- under the caption
    /// "the gap is how much you are fronting". The gap is not that: a settlement is a
    /// transfer rather than an expense, so the series never contained a repayment and the
    /// difference took no account of anything anybody had paid back. Somebody square with
    /// their flatmates read months of being owed hundreds. Asserted on the request rather
    /// than only on the markup, because the read is what made the claim available to draw.
    /// </remarks>
    [Fact]
    public void The_page_neither_draws_nor_asks_for_the_exposure_series()
    {
        var page = RenderView();

        Assert.Empty(page.FindAll(".gs-chart"));
        Assert.DoesNotContain("fronting", page.Markup, StringComparison.OrdinalIgnoreCase);

        _client.Verify(
            client => client.GetMonthlyExposureAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- the three views ----------------------------------------------------------------

    /// <summary>
    /// The view is the title. A chip under a heading called "Expenses" is what produced the
    /// confusion with a group's own listing in the first place.
    /// </summary>
    [Theory]
    [InlineData(null, "All yours")]
    [InlineData("paid", "What you paid")]
    [InlineData("share", "What you owe")]
    // What the default used to be reached by. Still honoured, so an old bookmark lands on
    // the same view rather than on an error.
    [InlineData("everything", "All yours")]
    public void The_view_in_the_url_is_the_heading(string? view, string expected)
    {
        var page = RenderView(view);

        Assert.Equal(expected, page.Find("h1").TextContent.Trim());
    }

    /// <summary>
    /// An unknown view falls back to the default rather than to an empty screen. A stale
    /// bookmark is not a reason to answer nothing.
    /// </summary>
    [Fact]
    public void An_unknown_view_falls_back_to_the_default()
    {
        var page = RenderView("nonsense");

        Assert.Equal("All yours", page.Find("h1").TextContent.Trim());
    }

    /// <summary>
    /// "All yours" is what the page opens on.
    /// </summary>
    /// <remarks>
    /// It opened on "You paid", which answers a narrower question than most people arrive
    /// with: somebody who is not the household's main payer saw a nearly empty page and no
    /// indication that the other two views existed. Same shape of defect as the CLI's
    /// `--group`, one surface along -- a plausible answer to a question nobody asked.
    /// </remarks>
    [Fact]
    public void The_page_opens_on_all_yours()
    {
        var page = RenderView();

        Assert.Equal("All yours", page.Find("h1").TextContent.Trim());

        var chips = page.FindAll(".gs-chipbar .gs-chip");
        var active = chips.Single(chip => chip.ClassList.Contains("active"));

        Assert.Contains("All yours", active.TextContent);
    }

    /// <summary>
    /// The labels change with the view because the figures do. "Total paid" over a list of
    /// other people's expenses would be describing a set nobody asked about.
    /// </summary>
    [Theory]
    [InlineData("paid", "Expenses you paid", "You paid")]
    [InlineData("share", "Expenses you are in", "You owe")]
    [InlineData(null, "Expenses you are in", "Your share")]
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
    [InlineData("paid", false)]
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

    // ---- a write, from anywhere ----------------------------------------------------------

    /// <summary>
    /// Reads of the grid's rows, whichever of the two listings the view in force uses.
    /// </summary>
    /// <remarks>
    /// Counted across both rather than pinned to one: the page has three views over two
    /// listings, and which one is the default has changed once already. What these tests
    /// are about is that an announcement makes the page ask <em>again</em>, which is true
    /// of every view.
    /// </remarks>
    private int RowReads =>
        _client.Invocations.Count(i => i.Method.Name
            is nameof(ITransactionsClient.GetTransactionsAsync)
            or nameof(ITransactionsClient.GetTransactionSharesAsync));

    /// <summary>
    /// The figures and the rows are this page's own reads, so an announcement has to make
    /// it ask again. It used to only re-render, and a grid that asks the server for its
    /// rows shows, on a re-render, the rows it already has: an expense added from the
    /// button on this page did not appear in the list under it.
    /// </summary>
    [Fact]
    public async Task A_write_announced_anywhere_re_reads_the_figures_and_the_rows()
    {
        var page = RenderView();

        page.WaitForAssertion(() => Assert.True(RowReads >= 1));

        var asksBefore = _asks.Count;
        var rowsBefore = RowReads;

        await page.InvokeAsync(() => Changes.NotifyTransactionsChangedAsync());

        page.WaitForAssertion(() =>
        {
            Assert.True(_asks.Count > asksBefore);
            Assert.True(RowReads > rowsBefore);
        });
    }

    /// <summary>
    /// A group renamed changes the tag on every one of its rows, so it is a change to this
    /// listing as much as an expense is.
    /// </summary>
    [Fact]
    public async Task A_change_to_the_groups_re_reads_the_rows_too()
    {
        var page = RenderView();

        page.WaitForAssertion(() => Assert.True(RowReads >= 1));

        var rowsBefore = RowReads;

        await page.InvokeAsync(() => Changes.NotifyGroupsChangedAsync());

        page.WaitForAssertion(() => Assert.True(RowReads > rowsBefore));
    }

    // ---- what an empty list says --------------------------------------------------------

    /// <summary>The empty state's heading and the sentence under it.</summary>
    private static (string Heading, string Blurb) Empty(IRenderedComponent<Transactions> page)
    {
        page.WaitForElement(".gs-empty > span", TimeSpan.FromSeconds(5));

        return (page.Find(".gs-empty h3").TextContent.Trim(),
            page.Find(".gs-empty > span").TextContent.Trim());
    }

    /// <summary>
    /// The month the page opens on is the page's doing rather than the reader's, and the
    /// heading says so.
    /// </summary>
    /// <remarks>
    /// The heading turned on IsNarrowed, which counts any span at all -- and since the
    /// range starts at the current month that is true from the first frame. So a brand-new
    /// account opened on "Nothing matches", naming a filter nobody had set.
    /// </remarks>
    [Fact]
    public void A_list_nobody_has_filtered_does_not_say_nothing_matches()
    {
        var page = RenderView();

        var (heading, blurb) = Empty(page);

        Assert.Equal("Nothing here yet", heading);
        Assert.Contains("this month", blurb);
        Assert.Contains("All time", blurb);
    }

    /// <summary>
    /// Widening to all time reaches the first-run sentence, which is the one an account
    /// with nothing in it should be reading.
    /// </summary>
    /// <remarks>
    /// It was unreachable: every arm of the blurb behind IsNarrowed was taken while the
    /// range sat on the opening month, so the copy telling somebody to record their first
    /// expense could only be seen by a reader who had gone looking for it.
    /// </remarks>
    [Fact]
    public async Task Widening_to_all_time_reaches_the_first_run_sentence()
    {
        var page = RenderView();

        await PickSpanAsync(page, DateFilterPreset.AllTime);

        var (heading, blurb) = Empty(page);

        Assert.Equal("Nothing here yet", heading);
        Assert.Equal(
            "Nothing you have a share of yet. Record an expense and it will show up here.",
            blurb);
    }

    /// <summary>A span somebody picked is a filter, and both lines treat it as one.</summary>
    /// <remarks>
    /// And the advice goes with it: pointing at "All time" from a span somebody chose
    /// themselves is a note about a control they have already found.
    /// </remarks>
    [Fact]
    public async Task A_span_somebody_picked_is_read_as_a_filter()
    {
        var page = RenderView();

        await PickSpanAsync(page, DateFilterPreset.LastMonth);

        var (heading, blurb) = Empty(page);

        Assert.Equal("Nothing matches", heading);
        Assert.Equal(
            "Nothing for last month matches. Try a different name, group, category or date.",
            blurb);
    }

    /// <summary>
    /// Narrowed to one group over all time, the sentence points at the group's own ledger
    /// and names no span, because there is none to name.
    /// </summary>
    [Fact]
    public async Task An_empty_group_points_at_the_groups_own_ledger()
    {
        var page = RenderView();

        await PickLedgerAsync(page, GroupId.ToString());
        await PickSpanAsync(page, DateFilterPreset.AllTime);

        Assert.Equal(
            "Nothing of yours in Weekend in Lisbon. The whole of what this group has spent, "
            + "whoever paid for it, is on the group's own ledger.",
            Empty(page).Blurb);
    }

    // ---- the actions on a row -----------------------------------------------------------

    /// <summary>A share row, so the grid has something to hang actions off.</summary>
    private static ExpenseShareResponse Row(string name, Guid payer, bool paidByYou) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Amount = 40m,
        Share = 10m,
        PaidByYou = paidByYou,
        PaidByUserId = payer,
        PaidByUserName = paidByYou ? "Anabel Benitez" : "Daniel Rivero",
        GroupId = GroupId,
        GroupName = "Weekend in Lisbon",
        DateTime = DateTimeOffset.UtcNow
    };

    private static IReadOnlyList<IElement> Actions(IRenderedComponent<Transactions> page, string prefix) =>
        page.FindAll("button[aria-label^='" + prefix + "']");

    /// <summary>
    /// Every row can be edited and deleted, whoever paid for it.
    /// </summary>
    /// <remarks>
    /// The buttons were gated on whether the caller was the payer, which is stricter than
    /// the API: both write endpoints reach a transaction through the caller's group
    /// membership, so every row in this listing is one the server would accept a change
    /// to. A shared expense entered with the wrong amount could be seen by four people and
    /// corrected by one -- and the group's own ledger, which never gated these, offered
    /// the buttons for the same row.
    /// </remarks>
    [Fact]
    public void Every_row_can_be_edited_and_deleted_whoever_paid()
    {
        _rows =
        [
            Row("Rent", Guid.NewGuid(), paidByYou: true),
            Row("Big shop", Guid.NewGuid(), paidByYou: false)
        ];

        var page = RenderView();

        page.WaitForAssertion(() => Assert.Equal(2, Actions(page, "Edit ").Count));

        Assert.Equal(2, Actions(page, "Delete ").Count);
        Assert.Contains(Actions(page, "Edit "),
            button => button.GetAttribute("aria-label") == "Edit Big shop");
    }

    /// <summary>
    /// Every row opens its own details, and the eye is the way in.
    /// </summary>
    /// <remarks>
    /// There was none. The dialog is reachable from the home page's two lists and was
    /// reachable from nowhere on the page devoted to expenses: the grid's rows are not
    /// links and their cells carry their own controls, so clicking one does nothing. The
    /// screen showing how a single expense was divided could not be opened from the screen
    /// listing the expenses.
    /// </remarks>
    [Fact]
    public async Task Every_row_opens_its_own_details()
    {
        var wanted = Row("Big shop", Guid.NewGuid(), paidByYou: false);
        _rows = [Row("Rent", Guid.NewGuid(), paidByYou: true), wanted];

        var page = RenderView();

        page.WaitForAssertion(() => Assert.Equal(2, Actions(page, "View ").Count));

        await Actions(page, "View ")
            .First(button => button.GetAttribute("aria-label") == "View Big shop")
            .ClickAsync(new());

        Assert.Equal(wanted.Id, _viewed);
    }
}

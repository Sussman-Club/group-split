using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The bank inbox, and the span of days it can be narrowed to. A bank sends months at a
/// time, so the point of the span is being able to take one month of a backlog and deal
/// with it; these pin that picking one reaches the server, and that the page then says
/// what it is actually showing rather than what it holds altogether.
/// </summary>
/// <remarks>
/// The real state service over a mocked client, so the chip click is followed all the way
/// to the request. A mocked state service would only pin that the page calls a method.
/// </remarks>
public class InboxPageTest : ComponentTest
{
    private readonly Mock<IInboxClient> _inbox = new();
    private readonly Mock<IBankConnectionsClient> _connections = new();
    private readonly Mock<IBankCommands> _bank = new();

    private readonly List<(DateOnly? From, DateOnly? To)> _asks = [];

    private List<BankTransactionResponse> _rows;

    /// <summary>
    /// The day the component's clock calls today, read from that same clock.
    /// </summary>
    /// <remarks>
    /// Not <c>DateTimeOffset.UtcNow.Date</c>, which is the same day only on a machine at
    /// UTC. <see cref="LocalClock"/> falls back to the machine's offset when the JS runtime
    /// cannot be asked synchronously, as it cannot here, so a run in the small hours UTC
    /// resolves "last month" against yesterday's date on a machine behind UTC -- and once a
    /// month that is a different month.
    /// </remarks>
    private DateTime Today => Services.GetRequiredService<LocalClock>().Today;

    public InboxPageTest()
    {
        _rows = [Row("Lidl", 30m, new DateOnly(2026, 9, 1))];

        _inbox
            .Setup(client => client.GetInboxSummaryAsync(It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InboxSummaryResponse(_rows.Count(row => row.Status == InboxStatus.New)));

        _inbox
            .Setup(client => client.GetInboxAsync(It.IsAny<InboxStatus?>(), It.IsAny<DateOnly?>(),
                It.IsAny<DateOnly?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboxStatus? status, DateOnly? from, DateOnly? to, string _, bool? _, int? _, int? _,
                CancellationToken _) =>
            {
                _asks.Add((from, to));

                var matching = _rows
                    .Where(row => row.Status == (status ?? InboxStatus.New))
                    .Where(row => from is null || row.SpentOn >= from)
                    .Where(row => to is null || row.SpentOn <= to)
                    .ToList();

                return new PagedResponseOfBankTransactionResponse(matching, 1, 25, matching.Count);
            });

        _connections
            .Setup(client => client.GetBankConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BankConnectionsResponse(true, [new BankConnectionResponse(
                Guid.NewGuid(), "fake", "Fake Bank", BankConnectionState.Active,
                DateTimeOffset.UtcNow, null, [])]));

        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(_connections.Object);
        Services.AddSingleton(_bank.Object);
        Services.AddSingleton<IInboxStateService>(provider => new InboxStateService(
            _inbox.Object,
            _connections.Object,
            provider.GetRequiredService<LoadGuard>(),
            provider.GetRequiredService<DataChangeNotifier>(),
            provider.GetRequiredService<LocalClock>()));
    }

    /// <summary>
    /// Sets the span, through the filter component's own callback rather than by clicking
    /// into its menu. The presets live in a Mud popover, which renders outside the tree
    /// bUnit puts on the page; what these tests are about is what the page does with a
    /// span once one is chosen, and this drives exactly that wiring.
    /// </summary>
    private static Task PickAsync(IRenderedComponent<Inbox> page, DateFilterPreset preset)
    {
        var filter = page.FindComponent<DateRangeFilter>();

        return page.InvokeAsync(() => filter.Instance.ValueChanged.InvokeAsync(new DateFilter(preset)));
    }

    [Fact]
    public void The_spans_are_on_the_page_and_it_starts_on_all_time()
    {
        var page = Render<Inbox>();

        // The control is on the page and says what it is showing.
        Assert.Contains("All time", page.Find(".gs-chip-menu").TextContent);

        // Nothing narrowed yet, so the request carried no span -- and the heading says
        // nothing, because on all time the count is the badge's and only the badge's.
        Assert.Equal((null, null), Assert.Single(_asks));
        Assert.DoesNotContain("to sort out", page.Markup);
        Assert.DoesNotContain("1 transaction", page.Markup);
    }

    [Fact]
    public async Task Picking_a_month_asks_the_server_for_that_month_as_days()
    {
        var page = Render<Inbox>();

        await PickAsync(page, DateFilterPreset.LastMonth);

        var expected = new DateFilter(DateFilterPreset.LastMonth).Days(Today);

        // Both ends really are a span, rather than the two nulls that would also satisfy
        // the comparison below if the chip had reached nothing.
        Assert.NotNull(expected.From);
        Assert.Equal(1, expected.From!.Value.Day);
        Assert.NotNull(expected.To);

        // Days, not instants: a bank dates a row by the calendar, so the request does too.
        Assert.Equal((expected.From, expected.To), _asks.Last());
    }

    /// <summary>
    /// Narrowed and empty is not the same as empty. The bank has sent plenty; none of it is
    /// in the month being looked at, and saying "nothing waiting" would send somebody
    /// looking for a fault that is not there.
    /// </summary>
    [Fact]
    public async Task A_month_holding_none_of_the_rows_says_so_rather_than_saying_nothing_arrived()
    {
        var page = Render<Inbox>();

        // Dated far enough back that no preset the chips offer can contain it.
        _rows = [Row("Lidl", 30m, new DateOnly(2020, 1, 1))];

        await PickAsync(page, DateFilterPreset.LastMonth);

        Assert.Contains("Nothing in this range", page.Markup);
        Assert.DoesNotContain("Nothing waiting", page.Markup);

        // And the heading describes the span rather than the badge, which still counts
        // everything waiting whenever it is from.
        Assert.Contains("0 transactions last month.", page.Markup);
    }

    [Fact]
    public async Task Widening_back_to_all_time_asks_for_everything_again()
    {
        var page = Render<Inbox>();

        await PickAsync(page, DateFilterPreset.LastMonth);
        await PickAsync(page, DateFilterPreset.AllTime);

        Assert.Equal((null, null), _asks.Last());

        // The span's heading goes with the span: back on all time there is no count under
        // the title, rather than last month's left standing.
        Assert.DoesNotContain("last month.", page.Markup);
        Assert.DoesNotContain("to sort out", page.Markup);
    }

    private static BankTransactionResponse Row(string merchant, decimal amount, DateOnly date) =>
        new(Guid.NewGuid(), date, amount, "USD", merchant.ToUpperInvariant(), merchant,
            null, null, null, null, null, null, null, false, InboxStatus.New, [], null,
            "Everyday", "Fake Bank");

    /// <summary>
    /// The same row, showing an expense it might already be: the same money to the cent, so
    /// a confident match.
    /// </summary>
    private BankTransactionResponse Flagged(string merchant, decimal amount) =>
        Row(merchant, amount, DateOnly.FromDateTime(Today)) with
        {
            PossibleDuplicates = [Match(merchant, amount, 0m, MatchConfidence.Confident)]
        };

    /// <summary>
    /// The same row, showing something whose amounts do not quite agree -- a tip, or a figure
    /// somebody rounded. Worth answering and not worth a warning.
    /// </summary>
    private BankTransactionResponse Possibly(string merchant, decimal amount, decimal apart = 6m) =>
        Row(merchant, amount, DateOnly.FromDateTime(Today)) with
        {
            PossibleDuplicates = [Match("Dinner", amount - apart, apart, MatchConfidence.Possible)]
        };

    private static ExpenseMatchResponse Match(string name, decimal amount, decimal apart,
        MatchConfidence confidence) =>
        new(Guid.NewGuid(), name, amount, "USD", DateTimeOffset.UtcNow, Guid.NewGuid(), "Home", "Anabel",
            apart, apart == 0m ? 0 : 2, confidence);

    /// <summary>Money coming in, which can be ignored but never filed as an expense.</summary>
    private BankTransactionResponse Credit(string merchant, decimal amount) =>
        Row(merchant, -amount, DateOnly.FromDateTime(Today));

    private static IReadOnlyList<IElement> Checkboxes(IRenderedComponent<Inbox> page) =>
        page.FindAll(".gs-row input[type=checkbox]");

    private static Task TickAsync(IRenderedComponent<Inbox> page, int index) =>
        Checkboxes(page)[index].ChangeAsync(new ChangeEventArgs { Value = true });

    private static Task PressAsync(IRenderedComponent<Inbox> page, string label) =>
        page.FindAll(".gs-selection-bar button")
            .First(button => button.TextContent.Contains(label)).ClickAsync(new());

    // ---- how loudly a suggestion is said --------------------------------------------

    /// <summary>
    /// The same money to the cent is a claim worth a warning on a row nobody has touched.
    /// </summary>
    [Fact]
    public void A_confident_match_arrives_as_a_warning()
    {
        _rows = [Flagged("Costco", 87.15m)];

        var page = Render<Inbox>();

        var alert = page.Find(".gs-queue .mud-alert");

        Assert.Contains("mud-alert-text-warning", alert.ClassName);
        Assert.Contains("You may already have recorded this", alert.TextContent);
    }

    /// <summary>
    /// A figure within a quarter of the charge is not. Measured over real spending it lands
    /// on unrelated money often enough that a warning for it is how somebody learns to stop
    /// reading them -- so it is said on the same surface in a plainer voice, and it says why
    /// it is being offered.
    /// </summary>
    [Fact]
    public void A_possible_match_arrives_as_a_note_and_not_a_warning()
    {
        _rows = [Possibly("Trattoria da Enzo", 46m)];

        var page = Render<Inbox>();

        var alert = page.Find(".gs-queue .mud-alert");

        Assert.DoesNotContain("mud-alert-text-warning", alert.ClassName);
        Assert.Contains("mud-alert-text-normal", alert.ClassName);
        Assert.Contains("could be one you have already recorded", alert.TextContent);
        Assert.Contains("6.00 apart", alert.TextContent);
    }

    /// <summary>
    /// Every candidate, not only the best-ranked one. The ranking can be wrong, and while
    /// the row showed one slot a coincidence a few cents nearer took it and kept the real
    /// duplicate off the screen altogether.
    /// </summary>
    [Fact]
    public void Every_candidate_is_listed_and_not_only_the_best_ranked_one()
    {
        _rows =
        [
            Row("Trattoria da Enzo", 46m, DateOnly.FromDateTime(Today)) with
            {
                PossibleDuplicates =
                [
                    Match("Dinner", 46m, 0m, MatchConfidence.Confident),
                    Match("Taxi home", 44m, 2m, MatchConfidence.Possible)
                ]
            }
        ];

        var page = Render<Inbox>();

        var alert = page.Find(".gs-queue .mud-alert");

        Assert.Equal(2, page.FindAll(".gs-queue .gs-match-line").Count);
        Assert.Contains("Dinner", alert.TextContent);
        Assert.Contains("Taxi home", alert.TextContent);
    }

    /// <summary>
    /// Both grades are counted in what the header says needs answering, because filing is
    /// refused over both.
    /// </summary>
    [Fact]
    public void The_header_counts_a_row_whose_match_is_only_possible()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Possibly("Trattoria da Enzo", 46m)];

        var page = Render<Inbox>();

        Assert.Contains("1 may already be recorded", page.Find(".gs-list-head").TextContent);
    }

    // ---- selecting several rows -----------------------------------------------------

    /// <summary>
    /// Select-all means all of them. A row with a suggestion on it is ticked like any other
    /// -- ignoring one in bulk is perfectly safe, and it is only *adding* it that has to
    /// wait for a person.
    /// </summary>
    [Fact]
    public async Task Select_all_covers_a_row_that_may_already_be_recorded()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Flagged("Costco", 87.15m), Row("Uber", 12m, DateOnly.FromDateTime(Today))];

        var page = Render<Inbox>();

        await page.Find(".gs-list-head input[type=checkbox]").ChangeAsync(
            new ChangeEventArgs { Value = true });

        Assert.Contains("3 selected", page.Find(".gs-selection-bar").TextContent);

        // And the header still says which of them will need answering rather than adding.
        Assert.Contains("may already be recorded", page.Find(".gs-list-head").TextContent);
    }

    /// <summary>
    /// The safety rule, where it now lives. Sending a flagged row in bulk would either walk
    /// into the refusal or -- much worse -- have to assert that the same money really was
    /// paid twice on somebody's behalf, and record it twice.
    /// </summary>
    [Fact]
    public async Task Keeping_the_selection_personal_leaves_a_flagged_row_for_a_person()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Flagged("Costco", 87.15m)];

        var page = Render<Inbox>();

        await page.Find(".gs-list-head input[type=checkbox]").ChangeAsync(
            new ChangeEventArgs { Value = true });
        await PressAsync(page, "Keep personal");

        _bank.Verify(bank => bank.KeepPersonalManyAsync(
            It.Is<IReadOnlyList<(Guid Id, string Title)>>(sent =>
                sent.Count == 1 && sent[0].Title == "Lidl"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The same for the softer grade, which is the commoner one: filing is refused over it
    /// too, so it is not something a bulk add may quietly push through.
    /// </summary>
    [Fact]
    public async Task Keeping_the_selection_personal_leaves_a_merely_similar_row_alone_as_well()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Possibly("Trattoria da Enzo", 46m)];

        var page = Render<Inbox>();

        await page.Find(".gs-list-head input[type=checkbox]").ChangeAsync(
            new ChangeEventArgs { Value = true });
        await PressAsync(page, "Keep personal");

        _bank.Verify(bank => bank.KeepPersonalManyAsync(
            It.Is<IReadOnlyList<(Guid Id, string Title)>>(sent =>
                sent.Count == 1 && sent[0].Title == "Lidl"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Ignoring is the bulk action a flagged row may take part in. It creates nothing, and
    /// filing that row later is still checked -- so there is nothing to protect it from.
    /// </summary>
    [Fact]
    public async Task Ignoring_the_selection_covers_a_flagged_row_too()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Flagged("Costco", 87.15m)];

        var page = Render<Inbox>();

        await page.Find(".gs-list-head input[type=checkbox]").ChangeAsync(
            new ChangeEventArgs { Value = true });
        await PressAsync(page, "Ignore");

        _bank.Verify(bank => bank.IgnoreManyAsync(
            It.Is<IReadOnlyList<(Guid Id, string Title)>>(sent => sent.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ignoring_the_selection_sends_every_row_that_was_ticked()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Row("Uber", 12m, DateOnly.FromDateTime(Today))];

        var page = Render<Inbox>();

        await TickAsync(page, 0);
        await TickAsync(page, 1);
        await PressAsync(page, "Ignore");

        _bank.Verify(bank => bank.IgnoreManyAsync(
            It.Is<IReadOnlyList<(Guid Id, string Title)>>(sent => sent.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A refund is not an expense and there is no other kind of transaction to file it as,
    /// so it is left out rather than sent and refused one row at a time.
    /// </summary>
    [Fact]
    public async Task Keeping_the_selection_personal_leaves_money_coming_in_alone()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today)), Credit("Amazon", 19.50m)];

        var page = Render<Inbox>();

        await TickAsync(page, 0);
        await TickAsync(page, 1);
        await PressAsync(page, "Keep personal");

        _bank.Verify(bank => bank.KeepPersonalManyAsync(
            It.Is<IReadOnlyList<(Guid Id, string Title)>>(sent =>
                sent.Count == 1 && sent[0].Title == "Lidl"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Selecting is offered over the queue and not over decisions already taken.
    /// </summary>
    [Fact]
    public async Task The_added_and_ignored_filters_offer_no_selection()
    {
        _rows = [Row("Lidl", 30m, DateOnly.FromDateTime(Today))];

        var page = Render<Inbox>();

        Assert.NotEmpty(Checkboxes(page));

        await page.FindAll("button").First(button => button.TextContent.Trim() == "Ignored")
            .ClickAsync(new());

        Assert.Empty(Checkboxes(page));
    }
}

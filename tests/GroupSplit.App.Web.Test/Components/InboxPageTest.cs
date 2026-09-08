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
        Services.AddSingleton<IBankCommands>(Mock.Of<IBankCommands>());
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
            null, null, null, null, null, null, false, InboxStatus.New, null, null,
            "Everyday", "Fake Bank");
}

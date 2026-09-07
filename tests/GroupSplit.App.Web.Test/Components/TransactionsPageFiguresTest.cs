using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The personal Expenses page over its real state, driven the way a person drives it: click
/// a chip and read the two figures above the list.
/// </summary>
/// <remarks>
/// <see cref="TransactionsPageTest"/> mocks the state service, which is the right shape for
/// asking which figure each card reads -- but it hands the page a <c>MatchesSummary</c> that
/// is always correct and always there, so the path the reported defect runs down is the one
/// thing it cannot see. That path is: a chip moves the page's own field, the grid under it
/// asks for a page, and the state reads the page and the narrowed summary together on the
/// back of that one call. Nothing before this rendered the page over that.
/// <para>
/// The group's Expenses tab has the same coverage through <see cref="GroupExpensesTabTest"/>
/// and <see cref="GroupDetailExpensesTabTest"/>, because its cards read a summary it fetches
/// itself. This page's come through the grid, so they need their own.
/// </para>
/// </remarks>
public class TransactionsPageFiguresTest : ComponentTest
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Trip = Guid.NewGuid();

    /// <summary>
    /// The clock the page and the state share. Its own, rather than the base class's, so the
    /// dates below are built against the same "today" the chips resolve their spans from.
    /// </summary>
    private readonly LocalClock _clock = new(Mock.Of<IJSRuntime>());

    private readonly Mock<ITransactionsClient> _client = new();

    private readonly List<TransactionResponse> _expenses;

    public TransactionsPageFiguresTest()
    {
        var firstOfThisMonth = new DateTime(_clock.Today.Year, _clock.Today.Month, 1);

        _expenses =
        [
            // Last month, in a group. The only thing "Last month" finds.
            Expense("Taxi", 10m, firstOfThisMonth.AddMonths(-1).AddDays(4), Trip),
            // This month, in a group.
            Expense("Dinner", 20m, _clock.Today, Trip),
            // Years back, and in no group at all -- so it is the only thing "Personal" finds,
            // and it falls outside every span the chips offer.
            Expense("Bike", 40m, firstOfThisMonth.AddYears(-2), null)
        ];

        _client
            .Setup(client => client.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string? _, bool? personal,
                string? search, string? _, bool? _, int? page, int? pageSize, CancellationToken _) =>
            {
                var matched = Matching(from, to, personal, search).ToList();
                var size = pageSize ?? PageRequest.DefaultPageSize;
                var number = page ?? 1;

                return new PagedResponseOfTransactionResponse(
                    matched.OrderByDescending(expense => expense.DateTime)
                        .Skip((number - 1) * size)
                        .Take(size)
                        .ToList(),
                    number, size, matched.Count);
            });

        _client
            .Setup(client => client.GetTransactionsSummaryAsync(It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string? _, bool? personal,
                string? search, CancellationToken _) =>
            {
                var matched = Matching(from, to, personal, search).ToList();

                return new TransactionSummaryResponse(matched.Count, matched.Sum(expense => expense.Amount));
            });

        // Registered after the base class's, so these win: the same clock the dates above
        // were built from, and the real state rather than a mock of it.
        Services.AddSingleton(_clock);
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(Mock.Of<ITransactionCommands>());
        Services.AddSingleton<TransactionsTracker>();
        Services.AddSingleton<ITransactionsPageStateService, TransactionsPageStateService>();
    }

    private TransactionResponse Expense(string name, decimal amount, DateTime day, Guid? groupId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Amount = amount,
        // Noon, so the span's own ends -- midnight to the last tick of the last day -- cannot
        // decide the outcome by rounding.
        DateTime = new DateTimeOffset(day.Date.AddHours(12), _clock.Offset).ToUniversalTime(),
        GroupId = groupId,
        GroupName = groupId is null ? null : "Weekend in Lisbon",
        PaidByUserId = Me,
        PaidByUserName = "Me"
    };

    /// <summary>The server's own arithmetic, over the same filter for the page and the summary.</summary>
    private IEnumerable<TransactionResponse> Matching(
        DateTimeOffset? from, DateTimeOffset? to, bool? personal, string? search) =>
        _expenses.Where(expense =>
            (from is null || expense.DateTime >= from) &&
            (to is null || expense.DateTime <= to) &&
            (personal is null || (personal.Value ? expense.GroupId is null : expense.GroupId is not null)) &&
            (string.IsNullOrWhiteSpace(search) ||
             expense.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));

    private IRenderedComponent<Transactions> Render()
    {
        var page = Render<Transactions>();

        // The grid's first read goes out on its own first render, so nothing is asserted
        // until the cards are showing an answer rather than the zeros they start on.
        page.WaitForAssertion(() => Assert.Equal(("3", "$70.00"), Cards(page)), TimeSpan.FromSeconds(5));

        return page;
    }

    /// <summary>The big number on the first card, and the money on the second.</summary>
    private static (string Count, string Total) Cards(IRenderedComponent<Transactions> page)
    {
        var values = page.FindAll(".gs-stat-value");

        return (values[0].TextContent.Trim(), values[1].TextContent.Trim());
    }

    private static Task PickAsync(IRenderedComponent<Transactions> page, string label) =>
        page.FindAll(".gs-chip").First(chip => chip.TextContent.Trim() == label).ClickAsync(new());

    /// <summary>
    /// The reported defect on this screen: the list follows the span and the two figures
    /// above it keep the all-time count and total.
    /// </summary>
    [Fact]
    public async Task Picking_a_span_moves_both_figures_off_the_whole_ledger()
    {
        var page = Render();

        await PickAsync(page, "Last month");

        page.WaitForAssertion(() => Assert.Equal(("1", "$10.00"), Cards(page)), TimeSpan.FromSeconds(5));

        // And the rows below say the same thing, which is the disagreement the issue is about.
        Assert.Equal(1, Rows(page));
    }

    /// <summary>
    /// The same, from a narrowing that is already in force. A scope pill on its own narrows
    /// without bounding the dates, so the figures beside it are an all-time answer -- and a
    /// span picked next has to move them off it. Held separately because this is the one
    /// starting point from which a span that fails to land leaves genuinely all-time figures
    /// on the cards rather than zeros.
    /// </summary>
    [Fact]
    public async Task A_span_narrows_what_a_scope_pill_had_already_narrowed()
    {
        var page = Render();

        await page.FindAll("button").First(button => button.TextContent.Trim() == "Personal").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal(("1", "$40.00"), Cards(page)), TimeSpan.FromSeconds(5));

        await PickAsync(page, "This month");

        page.WaitForAssertion(() => Assert.Equal(("0", "$0.00"), Cards(page)), TimeSpan.FromSeconds(5));
        Assert.Equal(0, Rows(page));
    }

    /// <summary>
    /// Two spans in quick succession, which is what the chips invite: the second is the one
    /// on screen, so it is the one the cards have to end on however the two answers arrive.
    /// </summary>
    [Fact]
    public async Task The_last_span_clicked_is_the_one_the_figures_describe()
    {
        var page = Render();

        var last = PickAsync(page, "Last month");
        var thisOne = PickAsync(page, "This month");

        await Task.WhenAll(last, thisOne);

        page.WaitForAssertion(() => Assert.Equal(("1", "$20.00"), Cards(page)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Widening_back_to_all_time_puts_the_whole_ledger_back()
    {
        var page = Render();

        await PickAsync(page, "Last month");

        page.WaitForAssertion(() => Assert.Equal(("1", "$10.00"), Cards(page)), TimeSpan.FromSeconds(5));

        await PickAsync(page, "All time");

        page.WaitForAssertion(() => Assert.Equal(("3", "$70.00"), Cards(page)), TimeSpan.FromSeconds(5));
    }

    /// <summary>How many expense rows the grid is showing.</summary>
    private static int Rows(IRenderedComponent<Transactions> page) =>
        page.FindAll("tbody tr").Count(row => row.QuerySelector(".gs-grid-amount") is not null);
}

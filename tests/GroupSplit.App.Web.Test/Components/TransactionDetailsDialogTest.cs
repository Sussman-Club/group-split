using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// One expense, read — with original and itemized receipts folded into one section.
/// </summary>
/// <remarks>
/// The bill used to open in a dialog on top of this one, which on a phone is two full-screen
/// sheets, two scrims and two ways to close with nothing saying the first survived. The
/// unified receipt section keeps the source file and itemized reading in one surface, while
/// keeping both folded until somebody asks for them.
/// </remarks>
public class TransactionDetailsDialogTest : ComponentTest
{
    private static readonly Guid Me = Guid.NewGuid();
    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<IReceiptCommands> _bills = new();
    private readonly Mock<IUserLogin> _login = new();
    private readonly Guid _expense = Guid.NewGuid();

    public TransactionDetailsDialogTest()
    {
        _transactions
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = _expense,
                Name = "Glovo",
                MerchantName = "Glovo",
                Amount = 37.15m,
                DateTime = new DateTimeOffset(2026, 9, 3, 14, 20, 0, TimeSpan.Zero),
                PaidByUserName = "Loraine Monteagudo",
                PaidByUserId = Guid.NewGuid(),
                Splits = []
            });

        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_bills.Object);
        _login.SetupGet(login => login.User)
            .Returns(new UserInfo(Me, "Ana", "Benitez", "ana@example.com"));
        Services.AddSingleton(_login.Object);
    }

    private static ReceiptResponse Bill() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 37.15m, 0, 0, 37.15m, 0, true, true,
        [
            new ReceiptItemResponse(Guid.NewGuid(), "Chicken burrito", 19.05m, 1, 19.05m, 0, Guid.NewGuid(), "All for Anabel", null),
            new ReceiptItemResponse(Guid.NewGuid(), "Poke bowl", 18.10m, 1, 18.10m, 0, Guid.NewGuid(), "All for Loraine", null)
        ]);

    private async Task<IRenderedComponent<MudDialogProvider>> Open(ReceiptResponse? bill)
    {
        _bills.Setup(b => b.GetAsync(_expense, It.IsAny<CancellationToken>())).ReturnsAsync(bill);

        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        await provider.InvokeAsync(async () => await Services.GetRequiredService<IDialogService>()
            .ShowAsync<TransactionDetailsDialog>("Expense", new DialogParameters<TransactionDetailsDialog>
            {
                { d => d.TransactionId, _expense }
            }));

        return provider;
    }

    /// <summary>
    /// Shut to begin with: the shares and the figure are what somebody opened this for, and a
    /// receipt unrolled between them and the Close button is the state it was redesigned out
    /// of once already.
    /// </summary>
    [Fact]
    public async Task The_bill_is_summarised_and_folded_away()
    {
        var dialog = await Open(Bill());

        dialog.WaitForAssertion(() => Assert.Contains("View itemized receipt", dialog.Markup));
        Assert.Contains("2 items", dialog.Markup);
        Assert.DoesNotContain("Chicken burrito", dialog.Markup);
    }

    /// <summary>
    /// And opening it prints the paper here rather than anywhere else. Nothing about this
    /// leaves the dialog: no second sheet, no second way to close.
    /// </summary>
    [Fact]
    public async Task Opening_it_prints_the_bill_without_a_second_dialog()
    {
        var dialog = await Open(Bill());

        dialog.WaitForAssertion(() => Assert.Contains("View itemized receipt", dialog.Markup));

        dialog.FindAll("button").Single(b => b.TextContent.Contains("View itemized receipt")).Click();

        dialog.WaitForAssertion(() => Assert.Contains("Chicken burrito", dialog.Markup));
        Assert.Contains("Poke bowl", dialog.Markup);
        Assert.Contains("Hide itemized receipt", dialog.Markup);

        // One dialog, still. The bill is not a dialog anywhere in the app any more.
        Assert.Single(dialog.FindAll(".mud-dialog"));
    }

    [Fact]
    public async Task Folding_it_away_again_leaves_the_summary()
    {
        var dialog = await Open(Bill());

        dialog.WaitForAssertion(() => Assert.Contains("View itemized receipt", dialog.Markup));
        dialog.FindAll("button").Single(b => b.TextContent.Contains("View itemized receipt")).Click();
        dialog.WaitForAssertion(() => Assert.Contains("Hide itemized receipt", dialog.Markup));

        dialog.FindAll("button").Single(b => b.TextContent.Contains("Hide itemized receipt")).Click();

        dialog.WaitForAssertion(() => Assert.DoesNotContain("Chicken burrito", dialog.Markup));
        Assert.Contains("View itemized receipt", dialog.Markup);
    }

    /// <summary>
    /// Nearly every expense has no bill. Those get no itemised-receipt control, while the
    /// separate receipt section remains available for an attachment added later.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_bill_is_offered_nothing()
    {
        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Contains("Glovo", dialog.Markup));
        Assert.DoesNotContain("View itemized receipt", dialog.Markup);
        Assert.Contains("Receipts", dialog.Markup);
        Assert.Contains("No receipt added yet", dialog.Markup);
        Assert.Contains("Add receipt", dialog.Markup);
        Assert.DoesNotContain("Add an original receipt", dialog.Markup);
    }

    [Fact]
    public async Task A_broken_merchant_logo_in_details_uses_merchant_initials()
    {
        _transactions
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = _expense,
                Name = "Glovo",
                MerchantName = "Glovo",
                MerchantLogoUrl = "https://plaid-merchant-logos.plaid.com/glovo.png",
                Amount = 37.15m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserName = "Loraine Monteagudo",
                PaidByUserId = Guid.NewGuid(),
                Splits = []
            });

        var dialog = await Open(bill: null);
        dialog.WaitForAssertion(() => Assert.NotEmpty(dialog.FindAll("img.gs-mark-inline")));

        dialog.Find("img.gs-mark-inline").TriggerEvent("onerror", new EventArgs());

        Assert.Empty(dialog.FindAll("img.gs-mark-inline"));
        Assert.Equal("G", dialog.Find(".gs-mark-inline-fallback").TextContent.Trim());
    }

    [Fact]
    public async Task Saving_an_itemized_receipt_refreshes_the_parent_shares()
    {
        var initial = new TransactionDetailsResponse
        {
            Id = _expense,
            Name = "Dinner",
            Amount = 37.15m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = Me,
            PaidByUserName = "Ana Benitez",
            Splits = [new TransactionSplitResponse(Me, "Ana Benitez", 20m)]
        };
        var refreshed = new TransactionDetailsResponse
        {
            Id = _expense,
            Name = "Dinner",
            Amount = 37.15m,
            DateTime = initial.DateTime,
            PaidByUserId = Me,
            PaidByUserName = "Ana Benitez",
            Splits = [new TransactionSplitResponse(Me, "Ana Benitez", 18m)]
        };
        var reads = 0;
        _transactions
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, int? _, CancellationToken _) => Task.FromResult(reads++ == 0 ? initial : refreshed));

        var bill = Bill() with { CanEdit = true };
        _bills.Setup(commands => commands.GetAsync(_expense, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);
        _bills.Setup(commands => commands.SaveAsync(_expense, It.IsAny<SaveReceiptRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);

        var dialog = await Open(bill);

        dialog.FindAll("button")
            .Single(button => button.TextContent.Contains("Edit itemized receipt"))
            .Click();
        dialog.WaitForAssertion(() => Assert.Contains("Save receipt", dialog.Markup));

        var names = dialog.FindComponents<MudTextField<string>>()
            .Where(field => field.Instance.Label == "Item")
            .ToList();
        await dialog.InvokeAsync(() => names[0].Instance.ValueChanged.InvokeAsync("Burrito corrected"));
        await dialog.InvokeAsync(() => names[1].Instance.ValueChanged.InvokeAsync("Poke corrected"));
        await dialog.FindAll("button")
            .Single(button => button.TextContent.Contains("Save receipt", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        dialog.WaitForAssertion(() => Assert.Contains("$18.00", dialog.Markup));
        _transactions.Verify(client => client.GetTransactionAsync(_expense, It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ---- A settlement ------------------------------------------------------------------

    private static readonly Guid Flat = Guid.NewGuid();

    private void ASettlement(SettlementCoverage? coverage)
    {
        _transactions
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = _expense,
                Kind = ActivityKind.Transfer,
                Name = "Settlement",
                Amount = 50m,
                DateTime = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
                GroupId = Flat,
                GroupName = "Flat",
                PaidByUserId = Guid.NewGuid(),
                PaidByUserName = "Loraine",
                PaidToUserId = Me,
                PaidToUserName = "Ana Benitez",
                Splits = [new TransactionSplitResponse(Me, "Ana Benitez", 50m)],
                Coverage = coverage
            });

        _groups
            .Setup(client => client.GetGroupTransactionsAsync(Flat, It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfTransactionResponse(
                [
                    new TransactionResponse
                    {
                        Id = Guid.NewGuid(),
                        Name = "Groceries",
                        Amount = 80m,
                        DateTime = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
                        PaidByUserName = "Ana Benitez"
                    }
                ], 1, 50, 1));
    }

    /// <summary>
    /// A settlement is who paid whom -- every one is named "Settlement", which says nothing --
    /// and it has no share for the reader to owe.
    /// </summary>
    [Fact]
    public async Task A_settlement_is_headed_by_who_paid_whom_and_shows_no_share()
    {
        ASettlement(coverage: null);

        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Contains("Loraine paid you", dialog.Markup));
        Assert.DoesNotContain("You owe", dialog.Markup);
        Assert.DoesNotContain("Split between", dialog.Markup);
        Assert.DoesNotContain("Expenses in the period", dialog.Markup);
    }

    [Fact]
    public async Task A_settlement_made_through_a_day_lists_the_expenses_in_its_period()
    {
        ASettlement(new SettlementCoverage(new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 1), 0m));

        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Contains("Groceries", dialog.Markup));
        Assert.Contains("1 Aug – 31 Aug 2026", dialog.Markup);
        Assert.Contains(">square<", dialog.Markup);
        Assert.DoesNotContain("No longer fully settled", dialog.Markup);
    }

    /// <summary>
    /// The case a stored list of covered expenses gets wrong: something dated inside the
    /// period and added after it was settled. The dialog is the only place that says so.
    /// </summary>
    [Fact]
    public async Task A_period_that_is_no_longer_square_says_what_is_still_owed()
    {
        ASettlement(new SettlementCoverage(new DateOnly(2026, 8, 31), null, 12.50m));

        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Contains("No longer fully settled", dialog.Markup));
        Assert.Contains("Loraine still owes you", dialog.Markup);
        Assert.Contains(">open<", dialog.Markup);
        Assert.Contains("$12.50", dialog.Markup);
    }

    // ---- when it happened (#252) ----------------------------------------------------------

    private void DatedAt(DateTimeOffset stored) =>
        _transactions
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = _expense,
                Name = "IKEA",
                Amount = 89.99m,
                DateTime = stored,
                PaidByUserName = "Anabel Benitez",
                PaidByUserId = Me,
                Splits = []
            });

    private static string When(IRenderedComponent<MudDialogProvider> dialog) =>
        dialog.FindAll(".gs-kv-row")
            .First(row => row.QuerySelector("dt")?.TextContent.Trim() == "When")
            .QuerySelector("dd")!.TextContent.Trim();

    /// <summary>
    /// The bank's date, alone: midnight UTC on the 9th is 19:00 on the 8th in New York and
    /// 10:00 on the 9th in Sydney, and neither time is anything the bank said.
    /// </summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(10)]
    public async Task A_bank_row_shows_the_banks_date_and_no_time(int hours)
    {
        ReaderAt(TimeSpan.FromHours(hours));
        DatedAt(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));

        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Equal("9 Oct 2026", When(dialog)));
    }

    [Theory]
    [InlineData(-5, "8 Oct 2026, 13:30")]
    [InlineData(10, "9 Oct 2026, 04:30")]
    public async Task A_recorded_moment_shows_its_day_and_time_where_the_reader_is(int hours, string shown)
    {
        ReaderAt(TimeSpan.FromHours(hours));
        DatedAt(new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.Zero));

        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Equal(shown, When(dialog)));
    }
}

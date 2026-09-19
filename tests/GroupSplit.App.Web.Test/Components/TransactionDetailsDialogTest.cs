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
    private readonly Mock<IReceiptCommands> _bills = new();
    private readonly Mock<IUserLogin> _login = new();
    private readonly Guid _expense = Guid.NewGuid();

    public TransactionDetailsDialogTest()
    {
        _transactions
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<CancellationToken>()))
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
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = _expense,
                Name = "Glovo",
                MerchantName = "Glovo",
                MerchantLogoUrl = "https://logos/glovo.png",
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
            .Setup(client => client.GetTransactionAsync(_expense, It.IsAny<CancellationToken>()))
            .Returns((Guid _, CancellationToken _) => Task.FromResult(reads++ == 0 ? initial : refreshed));

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
        _transactions.Verify(client => client.GetTransactionAsync(_expense,
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}

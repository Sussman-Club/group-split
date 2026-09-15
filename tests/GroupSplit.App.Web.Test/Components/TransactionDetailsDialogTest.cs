using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// One expense, read — and the bill folded under it.
/// </summary>
/// <remarks>
/// The bill used to open in a dialog on top of this one, which on a phone is two full-screen
/// sheets, two scrims and two ways to close with nothing saying the first survived. It folds
/// open in place now, so there is one surface either way: these say it is shut to begin with,
/// that opening it prints the paper without leaving, and that an expense with no bill is not
/// offered any of it.
/// </remarks>
public class TransactionDetailsDialogTest : ComponentTest
{
    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<IReceiptCommands> _bills = new();
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

        dialog.WaitForAssertion(() => Assert.Contains("View receipt", dialog.Markup));
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

        dialog.WaitForAssertion(() => Assert.Contains("View receipt", dialog.Markup));

        dialog.FindAll("button").Single(b => b.TextContent.Contains("View receipt")).Click();

        dialog.WaitForAssertion(() => Assert.Contains("Chicken burrito", dialog.Markup));
        Assert.Contains("Poke bowl", dialog.Markup);
        Assert.Contains("Hide receipt", dialog.Markup);

        // One dialog, still. The bill is not a dialog anywhere in the app any more.
        Assert.Single(dialog.FindAll(".mud-dialog"));
    }

    [Fact]
    public async Task Folding_it_away_again_leaves_the_summary()
    {
        var dialog = await Open(Bill());

        dialog.WaitForAssertion(() => Assert.Contains("View receipt", dialog.Markup));
        dialog.FindAll("button").Single(b => b.TextContent.Contains("View receipt")).Click();
        dialog.WaitForAssertion(() => Assert.Contains("Hide receipt", dialog.Markup));

        dialog.FindAll("button").Single(b => b.TextContent.Contains("Hide receipt")).Click();

        dialog.WaitForAssertion(() => Assert.DoesNotContain("Chicken burrito", dialog.Markup));
        Assert.Contains("View receipt", dialog.Markup);
    }

    /// <summary>
    /// Nearly every expense has no bill. Those get no itemised-receipt control, while the
    /// separate original-file section remains available for an attachment added later.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_bill_is_offered_nothing()
    {
        var dialog = await Open(bill: null);

        dialog.WaitForAssertion(() => Assert.Contains("Glovo", dialog.Markup));
        Assert.DoesNotContain("View receipt", dialog.Markup);
        Assert.Contains("Receipt files", dialog.Markup);
        Assert.Contains("No receipt files attached yet.", dialog.Markup);
    }
}

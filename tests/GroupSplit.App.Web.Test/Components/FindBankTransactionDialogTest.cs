using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class FindBankTransactionDialogTest : ComponentTest
{
    [Fact]
    public async Task Picker_loads_unfiled_pages_and_returns_the_selected_transaction()
    {
        var inbox = new Mock<IInboxClient>();
        var first = Row("Coffee", "USD");
        var next = Row("Groceries", "USD");
        var linked = Row("Already recorded", "USD") with
        {
            TransactionId = Guid.NewGuid()
        };
        inbox.Setup(x => x.SearchReceiptBankTransactionsAsync(null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfBankTransactionResponse([first, linked], 1, 20, 21));
        inbox.Setup(x => x.SearchReceiptBankTransactionsAsync(null, 2, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfBankTransactionResponse([next], 2, 20, 21));
        Services.AddSingleton(inbox.Object);
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () => dialog = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<FindBankTransactionDialog>("Find bank transaction", new DialogParameters<FindBankTransactionDialog> { { x => x.Currency, "USD" } }));
        provider.WaitForAssertion(() => Assert.Contains("Coffee", provider.Markup));
        Assert.DoesNotContain("Already recorded", provider.Markup);
        await provider.FindAll("button").Single(x => x.TextContent.Contains("Load more")).ClickAsync(new MouseEventArgs());
        provider.WaitForAssertion(() => Assert.Contains("Groceries", provider.Markup));
        await provider.FindAll(".gs-match-choice").Single(x => x.TextContent.Contains("Groceries")).ClickAsync(new MouseEventArgs());
        Assert.Equal(next.Id, (await dialog.GetReturnValueAsync<BankTransactionResponse>())!.Id);
    }
    private static BankTransactionResponse Row(string name, string currency) => new(Guid.NewGuid(),
        new DateOnly(2026, 10, 1), 10, currency, name, name, null, null, null, null, null, null, null,
        false, InboxStatus.New, null, null, "Checking", "Bank");
}

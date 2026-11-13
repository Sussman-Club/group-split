using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptDialogOpeningTest : ComponentTest
{
    [Fact]
    public async Task Expense_dialog_opens_while_duplicate_check_is_pending()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Shop", Total = 10m }, []);
        var pending = new TaskCompletionSource<ICollection<ReceiptExpenseCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.SetupSequence(x => x.MatchReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]).Returns(pending.Task);
        Services.AddSingleton(client.Object);
        var groups = new Mock<IGroupsClient>();
        groups.Setup(x => x.GetGroupsAsync(null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(groups.Object);
        var merchants = new Mock<IMerchantCommands>();
        merchants.Setup(x => x.SearchAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(merchants.Object);
        Services.AddSingleton(Mock.Of<ITransactionCommands>());
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Add to group", page.Markup));
        var click = page.FindAll("button").Single(button => button.TextContent.Contains("Add to group"))
            .ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<CreateTransactionDialog>()));
        Assert.False(pending.Task.IsCompleted);
        pending.SetResult([]);
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync(new MouseEventArgs());
        await click;
    }
}

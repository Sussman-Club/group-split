using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptInboxReviewTest : ComponentTest
{
    public ReceiptInboxReviewTest() => Services.AddSingleton(Mock.Of<IMerchantCommands>());

    [Fact]
    public void A_match_can_be_dismissed_without_removing_the_receipt()
    {
        var id = Guid.NewGuid();
        var expense = Guid.NewGuid();
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.GetReceiptInboxAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptInboxResponse(id, null, "USD", new SaveReceiptRequest(), []));
        client.Setup(x => x.MatchReceiptInboxAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReceiptExpenseCandidate(expense, "Lunch", 12, "USD", DateTimeOffset.UtcNow)]);
        client.Setup(x => x.DismissReceiptInboxMatchAsync(id, expense, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Services.AddSingleton(client.Object);

        var page = Render<ReceiptInboxReview>(parameters => parameters.Add(p => p.Id, id));
        page.WaitForAssertion(() => Assert.Contains("Lunch", page.Markup));
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Not the same").Click();
        page.WaitForAssertion(() => Assert.DoesNotContain("Lunch", page.Markup));
        Assert.Contains("Find existing expense", page.Markup);
        client.Verify(x => x.DismissReceiptInboxMatchAsync(id, expense, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_opens_by_default_when_items_are_missing(bool hasItems)
    {
        var id = Guid.NewGuid();
        var details = new SaveReceiptRequest { Items = hasItems ? [new ReceiptItemInput { Name = "Lunch" }] : [] };
        var attachment = new ReceiptAttachmentResponse(Guid.NewGuid(), null, null, id, "receipt.pdf", "application/pdf", 10, DateTimeOffset.UtcNow);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.GetReceiptInboxAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptInboxResponse(id, null, "USD", details, [attachment]));
        client.Setup(x => x.MatchReceiptInboxAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var bank = new Mock<IInboxClient>();
        bank.Setup(x => x.GetInboxAsync(It.IsAny<InboxStatus?>(), It.IsAny<DateOnly?>(), It.IsAny<DateOnly?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResponseOfBankTransactionResponse([], 1, 100, 0));
        Services.AddSingleton(client.Object);
        Services.AddSingleton(bank.Object);

        var page = Render<ReceiptInboxReview>(parameters => parameters.Add(p => p.Id, id));

        page.WaitForAssertion(() => Assert.Equal(!hasItems, page.Find("details.gs-receipt-review-original").HasAttribute("open")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unfiled_receipt_offers_the_action_for_its_status(bool ignored)
    {
        var id = Guid.NewGuid();
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.GetReceiptInboxAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptInboxResponse(id, null, "USD", new SaveReceiptRequest(), []) { IsIgnored = ignored });
        client.Setup(x => x.MatchReceiptInboxAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(x => x.IgnoreInboxReceiptAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        client.Setup(x => x.RestoreInboxReceiptAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInboxReview>(parameters => parameters.Add(p => p.Id, id));
        var action = ignored ? "Restore receipt" : "Ignore receipt";
        page.WaitForAssertion(() => Assert.Contains(action, page.Markup));
        page.FindAll("button").Single(button => button.TextContent.Trim() == action).Click();
        if (ignored) client.Verify(x => x.RestoreInboxReceiptAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        else client.Verify(x => x.IgnoreInboxReceiptAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.EndsWith(ignored ? "/inbox/receipts" : "/inbox/receipts?status=ignored", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Added_receipt_shows_expense_and_only_available_actions()
    {
        var id = Guid.NewGuid();
        var expense = Guid.NewGuid();
        var attachment = new ReceiptAttachmentResponse(Guid.NewGuid(), expense, null, id, "receipt.jpg", "image/jpeg", 10, DateTimeOffset.UtcNow);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.GetReceiptInboxAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptInboxResponse(id, expense, "USD", new SaveReceiptRequest(), [attachment]) { ExpenseName = "Team lunch" });
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInboxReview>(parameters => parameters.Add(p => p.Id, id));
        page.WaitForAssertion(() => Assert.Contains("Team lunch", page.Markup));
        Assert.Contains("View expense", page.Markup);
        Assert.DoesNotContain("Ignore receipt", page.Markup);
        Assert.DoesNotContain("Edit receipt details", page.Markup);
        Assert.Contains($"/api/transactions/{expense}/receipt-attachments/{attachment.Id}?inline=true", page.Markup);
        client.Verify(x => x.MatchReceiptInboxAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

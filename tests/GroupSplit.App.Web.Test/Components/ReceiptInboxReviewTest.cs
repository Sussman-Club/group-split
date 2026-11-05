using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptInboxReviewTest : ComponentTest
{
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
}

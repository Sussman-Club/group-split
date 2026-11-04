using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptInboxTest : ComponentTest
{
    [Fact]
    public async Task Search_matches_items_and_date_range_filters_receipts()
    {
        var client = new Mock<IReceiptsClient>();
        var first = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Shop", PurchaseDate = new DateOnly(2026, 1, 15), Items = [new ReceiptItemInput { Name = "Storage bins" }] }, []);
        var second = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Market", PurchaseDate = new DateOnly(2026, 2, 15), Items = [new ReceiptItemInput { Name = "Milk" }] }, []);
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        client.Setup(x => x.SuggestReceiptInboxFilingsAsync(It.IsAny<ReceiptInboxSuggestionsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Market", page.Markup));
        page.Find("input[aria-label='Search merchants or items']").Input("BINS");
        page.WaitForAssertion(() => { Assert.Contains("Shop", page.Markup); Assert.DoesNotContain("Market", page.Markup); });
        var picker = page.FindComponent<DateRangeFilter>();
        await page.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(new DateFilter(DateFilterPreset.Custom, new DateTime(2026, 2, 1), new DateTime(2026, 2, 28))));
        page.WaitForAssertion(() => Assert.Contains("No receipts match your filters.", page.Markup));
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Clear filters").Click();
        page.WaitForAssertion(() => { Assert.Contains("Shop", page.Markup); Assert.Contains("Market", page.Markup); });
    }
    [Fact]
    public void Ignored_filter_shows_restore_and_refreshes_after_restoring()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Store" }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.SetupSequence(x => x.ListIgnoredReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]).ReturnsAsync([]);
        client.Setup(x => x.RestoreInboxReceiptAsync(receipt.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Ignored").Click();
        page.WaitForAssertion(() => Assert.Contains("Store", page.Markup));
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Restore").Click();
        page.WaitForAssertion(() => Assert.Contains("Nothing ignored", page.Markup));
        client.Verify(x => x.RestoreInboxReceiptAsync(receipt.Id, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public void Suggestion_arriving_after_receipts_is_rendered_in_virtualized_row()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD",
            new SaveReceiptRequest { MerchantName = "Amazon", Total = 19.34m,
                Items = [new ReceiptItemInput { Name = "Storage bins" }] }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        Services.AddSingleton(client.Object);
        var pending = new TaskCompletionSource<ICollection<ReceiptInboxSuggestion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(x => x.SuggestReceiptInboxFilingsAsync(It.IsAny<ReceiptInboxSuggestionsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);

        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Amazon", page.Markup));
        pending.SetResult([new ReceiptInboxSuggestion(receipt.Id, new FilingDestinationSuggestion(Guid.NewGuid(), "Home", false, Guid.NewGuid(), "Groceries", .95))]);
        page.WaitForAssertion(() => Assert.Contains("Review: Home", page.Markup));
    }
}

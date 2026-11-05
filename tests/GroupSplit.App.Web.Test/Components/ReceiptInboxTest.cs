using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Microsoft.AspNetCore.Components.Web;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptInboxTest : ComponentTest
{
    [Fact]
    public async Task Upload_shows_only_the_dialog_loading_indicator()
    {
        var pending = new TaskCompletionSource<ReceiptAttachmentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(x => x.UploadInboxReceiptFileAsync(It.IsAny<Guid>(), It.IsAny<FileParameter>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        Services.AddSingleton(client.Object);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        await page.Find(".gs-fab").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ReceiptFilePicker>()));

        var upload = dialogs.InvokeAsync(() => dialogs.FindComponent<ReceiptFilePicker>().Instance.FileChanged.InvokeAsync(
            new Microsoft.AspNetCore.Components.Forms.InputFileChangeEventArgs(
                [new BufferedBrowserFile("receipt.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1])])));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindAll(".gs-file-picker-status[role='status']")));
        Assert.Empty(page.FindAll(".mud-progress-linear"));
        Assert.DoesNotContain("Importing receipt", page.Markup);

        pending.SetResult(new ReceiptAttachmentResponse(Guid.NewGuid(), null, null, Guid.NewGuid(), "receipt.jpg", "image/jpeg", 1, DateTimeOffset.UtcNow));
        await upload;
    }

    [Fact]
    public async Task Upload_reads_new_receipts_before_opening_the_review_page()
    {
        var attachmentId = Guid.NewGuid();
        var details = new SaveReceiptRequest { MerchantName = "Store", Total = 10m };
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(x => x.UploadInboxReceiptFileAsync(It.IsAny<Guid>(), It.IsAny<FileParameter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, FileParameter _, CancellationToken _) => new ReceiptAttachmentResponse(attachmentId, null, null, id, "receipt.jpg", "image/jpeg", 1, DateTimeOffset.UtcNow));
        client.Setup(x => x.TranscribeInboxReceiptAsync(It.IsAny<Guid>(), attachmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptDraftResponse("test", details));
        client.Setup(x => x.SaveExtractedInboxReceiptAsync(It.IsAny<Guid>(), details, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, SaveReceiptRequest _, CancellationToken _) => new ReceiptInboxResponse(id, null, "USD", details, []));
        Services.AddSingleton(client.Object);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();

        await page.Find(".gs-fab").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ReceiptFilePicker>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ReceiptFilePicker>().Instance.FileChanged.InvokeAsync(
            new Microsoft.AspNetCore.Components.Forms.InputFileChangeEventArgs(
                [new BufferedBrowserFile("receipt.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1])])));

        client.Verify(x => x.TranscribeInboxReceiptAsync(It.IsAny<Guid>(), attachmentId, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.SaveExtractedInboxReceiptAsync(It.IsAny<Guid>(), details, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("/inbox/receipts/", Services.GetRequiredService<NavigationManager>().Uri);
    }

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
        Assert.Contains("q=BINS", Services.GetRequiredService<NavigationManager>().Uri);
        var picker = page.FindComponent<DateRangeFilter>();
        await page.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(new DateFilter(DateFilterPreset.Custom, new DateTime(2026, 2, 1), new DateTime(2026, 2, 28))));
        page.WaitForAssertion(() => Assert.Contains("No receipts match your filters.", page.Markup));
        Assert.Contains("from=2026-02-01", Services.GetRequiredService<NavigationManager>().Uri);
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Clear filters").Click();
        page.WaitForAssertion(() => { Assert.Contains("Shop", page.Markup); Assert.Contains("Market", page.Markup); });
    }
    [Fact]
    public void Opens_the_status_search_and_dates_given_in_the_address()
    {
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(x => x.ListIgnoredReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Market", PurchaseDate = new DateOnly(2026, 2, 15), Items = [new ReceiptItemInput { Name = "Milk" }] }, []),
            new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Shop", PurchaseDate = new DateOnly(2026, 1, 15), Items = [new ReceiptItemInput { Name = "Milk" }] }, [])]);
        Services.AddSingleton(client.Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/inbox/receipts?status=ignored&q=milk&from=2026-02-01&to=2026-02-28");
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => { Assert.Contains("Market", page.Markup); Assert.DoesNotContain("Shop", page.Markup); });
        client.Verify(x => x.ListIgnoredReceiptInboxAsync(It.IsAny<CancellationToken>()), Times.Once);
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
    public void Waiting_receipt_without_a_match_or_suggestion_offers_add_to_group()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Shop" }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        Services.AddSingleton(client.Object);

        var page = Render<ReceiptInbox>();

        page.WaitForAssertion(() => Assert.Contains("Add to group", page.Markup));
    }

    [Fact]
    public void Matching_expense_replaces_add_to_group()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest
        { MerchantName = "Shop", Total = 10m, PurchaseDate = new DateOnly(2026, 11, 4) }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.Setup(x => x.MatchReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReceiptExpenseCandidate(Guid.NewGuid(), "Groceries", 10m, "USD", new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero))]);
        Services.AddSingleton(client.Object);

        var page = Render<ReceiptInbox>();

        page.WaitForAssertion(() => Assert.Contains("Attach to Groceries", page.Markup));
        Assert.DoesNotContain("Add to group", page.Markup);
    }

    [Fact]
    public void Expense_with_receipt_is_not_offered_as_an_attachment_target()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest
        { MerchantName = "Shop", Total = 10m, PurchaseDate = new DateOnly(2026, 11, 4) }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.Setup(x => x.MatchReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReceiptExpenseCandidate(Guid.NewGuid(), "Groceries", 10m, "USD", new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), HasReceipt: true)]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();

        page.WaitForAssertion(() => Assert.Contains("Add to group", page.Markup));
        Assert.DoesNotContain("Attach to Groceries", page.Markup);
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

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
    public async Task Unavailable_importer_offers_installation_instructions()
    {
        var module = JSInterop.SetupModule("./_content/GroupSplit.App.Shared/js/receipt-importer.js");
        module.Setup<bool>("openReceiptImporter", _ => true).SetResult(false);
        var page = Render<ReceiptInbox>();

        await page.Find("button.gs-receipt-import-action").ClickAsync(new MouseEventArgs());

        var link = page.Find(".gs-receipt-import-help a");
        Assert.Equal("/receipt-importer/install", link.GetAttribute("href"));
        Assert.Contains("Install or update", link.TextContent);
        var instructions = Render<ReceiptImporterInstall>();
        var links = instructions.FindAll("a[target='_blank']").Select(a => a.GetAttribute("href")).ToArray();
        Assert.Contains("https://microsoftedge.microsoft.com/addons/detail/aaclkbbjgicajpnjoigfjnlmpiocngmp", links);
        Assert.Contains("https://chromewebstore.google.com/detail/groupsplit-receipt-import/ajofnofdakhcjkgjijkdenieilgndpeb", links);
        Assert.Contains("Microsoft Edge", instructions.Markup);
    }

    [Fact]
    public async Task Android_explains_computer_import_and_offers_receipt_capture()
    {
        Services.AddSingleton(new GroupSplit.App.Shared.Services.AppLinks(new Uri("https://groupsplit.example/")) { IsAndroidNative = true });
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Import store receipts on a computer", page.Markup));
        Assert.Contains("same account", page.Markup);
        Assert.Empty(page.FindAll(".gs-receipt-import-action"));
        Assert.DoesNotContain("Import from a store", page.Markup);
        await page.FindAll("button").Single(button => button.TextContent.Contains("Upload a receipt")).ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ReceiptFilePicker>()));
    }

    [Fact]
    public async Task Empty_waiting_inbox_offers_upload_and_store_import()
    {
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(client.Object);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".gs-receipt-empty-actions button").Count));
        Assert.Contains("Import from a store", page.Find(".gs-receipt-empty-actions").TextContent);
        await page.FindAll(".gs-receipt-empty-actions button").Single(b => b.TextContent.Contains("Upload a receipt")).ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ReceiptFilePicker>()));
    }

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

    [Theory]
    [InlineData("Costco")]
    [InlineData("Costco Wholesale")]
    [InlineData(" costco wholesale ")]
    public void Costco_receipts_use_the_bundled_logo(string merchant)
    {
        var client = new Mock<IReceiptsClient>();
        var row = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = merchant }, []);
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([row]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("costco.svg", page.Markup));
    }

    [Fact]
    public async Task Duplicate_comparison_identifies_both_receipts_and_the_originals()
    {
        var first = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "First", Total = 12 },
            [new ReceiptAttachmentResponse(Guid.NewGuid(), null, null, Guid.NewGuid(), "first.pdf", "application/pdf", 1, DateTimeOffset.UtcNow)]);
        var second = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Second", Total = 12 },
            [new ReceiptAttachmentResponse(Guid.NewGuid(), null, null, Guid.NewGuid(), "second.pdf", "application/pdf", 1, DateTimeOffset.UtcNow)]);
        first = first with { SuggestedDuplicateOfId = second.Id };
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first]);
        client.Setup(x => x.GetReceiptInboxAsync(second.Id, It.IsAny<CancellationToken>())).ReturnsAsync(second);
        Services.AddSingleton(client.Object);
        var dialog = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Compare receipts", page.Markup));
        var review = page.FindAll("button").Single(x => x.TextContent.Contains("Compare receipts")).ClickAsync(new MouseEventArgs());
        dialog.WaitForAssertion(() => Assert.Contains("Inbox receipt", dialog.Markup));
        Assert.Contains("Matching receipt", dialog.Markup);
        Assert.DoesNotContain("first.pdf", dialog.Markup);
        Assert.DoesNotContain("second.pdf", dialog.Markup);
        Assert.Empty(dialog.FindAll(".gs-pdf-preview, img"));
        Assert.Contains("Keep both", dialog.Markup);
        await dialog.FindAll("button").Single(x => x.TextContent.Contains("Keep both")).ClickAsync(new MouseEventArgs());
        await review;
        client.Verify(x => x.DeleteReceiptInboxAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Duplicate_review_can_delete_the_other_unfiled_receipt()
    {
        var first = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "First" }, []);
        var second = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Second" }, []);
        first = first with { PossibleDuplicates = [new ReceiptDuplicateCandidate(second.Id, null)] };
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first]);
        client.Setup(x => x.GetReceiptInboxAsync(second.Id, It.IsAny<CancellationToken>())).ReturnsAsync(second);
        client.Setup(x => x.DeleteReceiptInboxAsync(second.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Services.AddSingleton(client.Object);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Compare receipts", page.Markup));
        var pending = page.FindAll("button").Single(x => x.TextContent.Trim() == "Compare receipts").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Equal(2, dialogs.FindAll("input[type=radio]").Count));
        Assert.True(dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Delete receipt").HasAttribute("disabled"));
        dialogs.FindAll("input[type=radio]")[1].Change(true);
        await dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Delete receipt").ClickAsync(new MouseEventArgs());
        await pending;
        client.Verify(x => x.DeleteReceiptInboxAsync(second.Id, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.DeleteReceiptInboxAsync(first.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Selection_mode_hides_controls_until_enabled_and_selects_dragged_rows()
    {
        var first = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "First" }, []);
        var second = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Second" }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("First", page.Markup));
        Assert.Empty(page.FindAll("input[type=checkbox]"));
        await page.FindAll("button").Single(x => x.TextContent.Trim() == "Select").ClickAsync(new MouseEventArgs());
        await page.InvokeAsync(() => page.Instance.SelectDraggedRows([first.Id.ToString(), second.Id.ToString()]));
        Assert.Equal(2, page.FindAll("input[type=checkbox]:checked").Count);
        Assert.Contains("Delete selected", page.Markup);
        await page.FindAll("button").Single(x => x.TextContent.Trim() == "Done").ClickAsync(new MouseEventArgs());
        Assert.Empty(page.FindAll("input[type=checkbox]"));
        client.Verify(x => x.DeleteReceiptInboxAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bulk_delete_requires_confirmation_before_deleting_selected_receipts()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Store" }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.Setup(x => x.DeleteReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Services.AddSingleton(client.Object);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Store", page.Markup));
        page.FindAll("button").Single(x => x.TextContent.Trim() == "Select").Click();
        page.Find("input[type=checkbox]").Change(true);
        var pending = page.FindAll("button").Single(x => x.TextContent.Trim() == "Delete selected").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("Delete selected receipts?", dialogs.Markup));
        client.Verify(x => x.DeleteReceiptInboxAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        await dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Delete").ClickAsync(new MouseEventArgs());
        await pending;
        client.Verify(x => x.DeleteReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Shows_a_possible_duplicate_warning_in_the_receipt_list()
    {
        var client = new Mock<IReceiptsClient>();
        var row = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Shop" }, [])
        {
            PossibleDuplicates = [new ReceiptDuplicateCandidate(Guid.NewGuid(), null)]
        };
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([row]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Possible duplicate receipt", page.Markup));
    }

    [Fact]
    public void A_confirmed_duplicate_suggests_review_before_deleting()
    {
        var client = new Mock<IReceiptsClient>();
        var row = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest { MerchantName = "Shop" }, [])
        {
            SuggestedDuplicateOfId = Guid.NewGuid()
        };
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([row]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        page.WaitForAssertion(() => Assert.Contains("Compare receipts", page.Markup));
        client.Verify(x => x.DeleteReceiptInboxAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public void Waits_for_both_suggestions_and_expense_matches_before_showing_receipts()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest
        {
            MerchantName = "Shop",
            Total = 10m,
            PurchaseDate = new DateOnly(2026, 11, 4),
            Items = [new ReceiptItemInput { Name = "Milk" }]
        }, []);
        var matches = new TaskCompletionSource<ICollection<ReceiptExpenseCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var suggestions = new TaskCompletionSource<ICollection<ReceiptInboxSuggestion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.Setup(x => x.MatchReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>())).Returns(matches.Task);
        client.Setup(x => x.SuggestReceiptInboxFilingsAsync(It.IsAny<ReceiptInboxSuggestionsRequest>(), It.IsAny<CancellationToken>())).Returns(suggestions.Task);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();
        Assert.DoesNotContain("Shop", page.Markup);
        matches.SetResult([new ReceiptExpenseCandidate(Guid.NewGuid(), "Groceries", 10m, "USD", new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero))]);
        Assert.DoesNotContain("Shop", page.Markup);
        suggestions.SetResult([]);
        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup));
        Assert.Contains("Shop", page.Markup);
        Assert.DoesNotContain("Add to group", page.Markup);
    }

    [Fact]
    public void Matching_expense_replaces_add_to_group()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest
        {
            MerchantName = "Shop",
            Total = 10m,
            PurchaseDate = new DateOnly(2026, 11, 4)
        }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.Setup(x => x.MatchReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReceiptExpenseCandidate(Guid.NewGuid(), "Groceries", 10m, "USD", new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero))]);
        Services.AddSingleton(client.Object);

        var page = Render<ReceiptInbox>();

        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup));
        Assert.DoesNotContain("Add to group", page.Markup);
    }

    [Fact]
    public async Task Attach_review_allows_selecting_a_different_match()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest
        {
            MerchantName = "Shop",
            Total = 10m,
            PurchaseDate = new DateOnly(2026, 11, 4)
        }, []);
        var first = new ReceiptExpenseCandidate(Guid.NewGuid(), "Groceries", 10m, "USD", DateTimeOffset.UtcNow);
        var second = new ReceiptExpenseCandidate(Guid.NewGuid(), "Lunch", 10m, "USD", DateTimeOffset.UtcNow);
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptMatchDialog>("Review", new DialogParameters<ReceiptMatchDialog>
            { { x => x.Row, receipt }, { x => x.Matches, new[] { first, second } }, { x => x.InitialId, first.Id } }));
        provider.WaitForAssertion(() => Assert.Contains("Lunch", provider.Markup));
        Assert.Contains("Find another expense", provider.Markup);
        Assert.Contains("Create a new expense", provider.Markup);
        Assert.Contains("Suggested match", provider.Markup);
        Assert.Equal("true", provider.FindAll("button[role=radio]").Single(button => button.TextContent.Contains("Groceries")).GetAttribute("aria-checked"));
        Assert.False(provider.FindAll("button").Single(button => button.TextContent.Trim() == "Attach to this expense").HasAttribute("disabled"));
        provider.FindAll("button[role=radio]").Single(button => button.TextContent.Contains("Lunch")).Click();
        provider.FindAll("button").Single(button => button.TextContent.Trim() == "Attach to this expense").Click();
        var answer = await reference!.GetReturnValueAsync<ReceiptMatchDialog.Answer>();
        Assert.Equal(ReceiptMatchDialog.Action.Attach, answer!.Action);
        Assert.Equal(second.Id, answer.ExpenseId);
    }

    [Fact]
    public void Expense_with_receipt_is_suggested_as_a_possible_duplicate()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new SaveReceiptRequest
        {
            MerchantName = "Shop",
            Total = 10m,
            PurchaseDate = new DateOnly(2026, 11, 4)
        }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        client.Setup(x => x.MatchReceiptInboxAsync(receipt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReceiptExpenseCandidate(Guid.NewGuid(), "Groceries", 10m, "USD", new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), HasReceipt: true)]);
        Services.AddSingleton(client.Object);
        var page = Render<ReceiptInbox>();

        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup));
        Assert.Contains("Possible duplicate receipt", page.Markup);
    }

    [Fact]
    public void Suggestion_is_shown_with_receipt_after_loading()
    {
        var receipt = new ReceiptInboxResponse(Guid.NewGuid(), null, "USD",
            new SaveReceiptRequest
            {
                MerchantName = "Amazon",
                Total = 19.34m,
                Items = [new ReceiptItemInput { Name = "Storage bins" }]
            }, []);
        var client = new Mock<IReceiptsClient>();
        client.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync([receipt]);
        Services.AddSingleton(client.Object);
        var pending = new TaskCompletionSource<ICollection<ReceiptInboxSuggestion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(x => x.SuggestReceiptInboxFilingsAsync(It.IsAny<ReceiptInboxSuggestionsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);

        var page = Render<ReceiptInbox>();
        Assert.DoesNotContain("Amazon", page.Markup);
        pending.SetResult([new ReceiptInboxSuggestion(receipt.Id, new FilingDestinationSuggestion(Guid.NewGuid(), "Home", false, Guid.NewGuid(), "Groceries", .95))]);
        page.WaitForAssertion(() => Assert.Contains("Home · Groceries", page.Markup));
    }
}

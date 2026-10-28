using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptSectionTest : ComponentTest
{
    private readonly Guid _expense = Guid.NewGuid();

    private static ReceiptResponse Bill(Guid expenseId) =>
        new(Guid.NewGuid(), expenseId, 37.15m, 0, 0, 37.15m, 0, true, true,
        [new ReceiptItemResponse(Guid.NewGuid(), "Chicken burrito", 37.15m, 1, 37.15m, 0,
            Guid.NewGuid(), "Everyone", new EvenSplitRuleDto())]);

    private ReceiptAttachmentResponse Attachment(string fileName = "dinner.jpg") =>
        new(Guid.NewGuid(), _expense, null, null, fileName, "image/jpeg", 2048, DateTimeOffset.UtcNow);

    [Fact]
    public void An_empty_section_stays_compact_until_the_person_adds_a_receipt()
    {
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptSection>(p => p.Add(c => c.TransactionId, _expense));

        Assert.Contains("Receipts", page.Markup);
        Assert.Contains("No receipt added yet", page.Markup);
        Assert.Contains("Add receipt", page.Markup);
        Assert.DoesNotContain("Add an original receipt", page.Markup);

        page.FindAll("button").Single(button => button.TextContent.Contains("Add receipt")).Click();

        dialogs.WaitForAssertion(() => Assert.Contains("Add an original receipt", dialogs.Markup));
        Assert.Contains("Reading it is optional", dialogs.Markup);
    }

    [Fact]
    public void It_keeps_the_original_and_itemized_receipt_as_separate_rows()
    {
        var attachment = Attachment();
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Receipt, Bill(_expense))
            .Add(c => c.Attachments, [attachment]));

        Assert.DoesNotContain("Original receipt", page.Markup);
        page.FindAll("button").Single(button => button.TextContent.Contains("Open receipts")).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Original receipt", dialogs.Markup));
        Assert.Contains("Original receipt", dialogs.Markup);
        Assert.Contains("dinner.jpg", dialogs.Markup);
        Assert.Contains("Itemized receipt", dialogs.Markup);

        Assert.Contains("Chicken burrito", dialogs.Markup);

        page.WaitForAssertion(() => Assert.Contains("Chicken burrito", dialogs.Markup));
        Assert.DoesNotContain("Hide itemized receipt", dialogs.Markup);
    }

    [Fact]
    public void View_original_uses_the_inline_file_url()
    {
        var attachment = Attachment();
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Attachments, [attachment]));

        page.FindAll("button").Single(button => button.TextContent.Contains("Open receipts")).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Original preview", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Contains("Original preview")).Click();

        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("img")));
        Assert.Contains("receipt-attachments", dialogs.Find("img").GetAttribute("src"));
        Assert.Contains("inline=true", dialogs.Find("img").GetAttribute("src"));
    }

    [Fact]
    public void View_original_renders_the_pdf_preview_with_download_available()
    {
        var attachment = Attachment("dinner.pdf") with { ContentType = "application/pdf" };
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Attachments, [attachment]));

        page.FindAll("button").Single(button => button.TextContent.Contains("Open receipts")).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Original preview", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Contains("Original preview")).Click();

        page.WaitForAssertion(() => Assert.Contains("PDF preview", dialogs.Markup));
        Assert.Empty(dialogs.FindAll("iframe"));
        Assert.Contains("Download PDF", dialogs.Markup);
        Assert.Single(dialogs.FindComponents<PdfReceiptPreview>());
        Assert.Single(dialogs.FindAll("canvas"));
    }

    [Fact]
    public void Read_and_remove_actions_are_forwarded_to_the_parent()
    {
        var attachment = Attachment("dinner.pdf") with { ContentType = "application/pdf" };
        ReceiptAttachmentResponse? read = null;
        ReceiptAttachmentResponse? removed = null;
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Attachments, [attachment])
            .Add(c => c.TranscribeRequested, value => read = value)
            .Add(c => c.DeleteRequested, value => removed = value));

        page.FindAll("button").Single(button => button.TextContent.Contains("Open receipts")).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Read receipt", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Contains("Read receipt")).Click();
        dialogs.FindAll("button").Single(button => button.TextContent.Contains("Remove")).Click();

        Assert.Equal(attachment, read);
        Assert.Equal(attachment, removed);
        Assert.Contains("dinner.pdf", dialogs.Markup);
    }

    [Fact]
    public void An_editable_itemized_receipt_forwards_the_edit_action()
    {
        var bill = Bill(_expense) with { CanEdit = true };
        ReceiptResponse? edited = null;
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Receipt, bill)
            .Add(c => c.EditRequested, value => edited = value));

        page.FindAll("button").Single(button => button.TextContent.Contains("Open receipts")).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Edit itemized receipt", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Contains("Edit itemized receipt")).Click();

        Assert.Same(bill, edited);
    }
}

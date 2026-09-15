using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;

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
        var page = Render<ReceiptSection>(p => p.Add(c => c.TransactionId, _expense));

        Assert.Contains("Receipts", page.Markup);
        Assert.Contains("No receipt added yet", page.Markup);
        Assert.Contains("Add receipt", page.Markup);
        Assert.DoesNotContain("Add an original receipt", page.Markup);

        page.FindAll("button").Single(button => button.TextContent.Contains("Add receipt")).Click();

        page.WaitForAssertion(() => Assert.Contains("Add an original receipt", page.Markup));
        Assert.Contains("Reading it is optional", page.Markup);
    }

    [Fact]
    public void It_keeps_the_original_and_itemized_receipt_as_separate_rows()
    {
        var attachment = Attachment();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Receipt, Bill(_expense))
            .Add(c => c.Attachments, [attachment]));

        Assert.Contains("Original receipt", page.Markup);
        Assert.Contains("dinner.jpg", page.Markup);
        Assert.Contains("Itemized receipt", page.Markup);
        Assert.Contains("View itemized receipt", page.Markup);
        Assert.DoesNotContain("Chicken burrito", page.Markup);

        page.FindAll("button").Single(button => button.TextContent.Contains("View itemized receipt")).Click();

        page.WaitForAssertion(() => Assert.Contains("Chicken burrito", page.Markup));
        Assert.Contains("Hide itemized receipt", page.Markup);
    }

    [Fact]
    public void View_original_uses_the_inline_file_url()
    {
        var attachment = Attachment();
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Attachments, [attachment]));

        page.FindAll("button").Single(button => button.TextContent.Contains("View original")).Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("img")));
        Assert.Contains("receipt-attachments", page.Find("img").GetAttribute("src"));
        Assert.Contains("inline=true", page.Find("img").GetAttribute("src"));
    }

    [Fact]
    public void View_original_shows_a_clear_fallback_for_pdf_files()
    {
        var attachment = Attachment("dinner.pdf") with { ContentType = "application/pdf" };
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Attachments, [attachment]));

        page.FindAll("button").Single(button => button.TextContent.Contains("View original")).Click();

        page.WaitForAssertion(() => Assert.Contains("PDF preview", page.Markup));
        Assert.Empty(page.FindAll("iframe"));
        Assert.Contains("This browser can’t show PDF pages inline", page.Markup);
        Assert.Contains("Download PDF", page.Markup);
    }

    [Fact]
    public void Read_and_remove_actions_are_forwarded_to_the_parent()
    {
        var attachment = Attachment("dinner.pdf") with { ContentType = "application/pdf" };
        ReceiptAttachmentResponse? read = null;
        ReceiptAttachmentResponse? removed = null;
        var page = Render<ReceiptSection>(p => p
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.Attachments, [attachment])
            .Add(c => c.TranscribeRequested, value => read = value)
            .Add(c => c.DeleteRequested, value => removed = value));

        page.FindAll("button").Single(button => button.TextContent.Contains("Read receipt")).Click();
        page.FindAll("button").Single(button => button.TextContent.Contains("Remove")).Click();

        Assert.Equal(attachment, read);
        Assert.Equal(attachment, removed);
        Assert.Contains("dinner.pdf", page.Markup);
    }
}

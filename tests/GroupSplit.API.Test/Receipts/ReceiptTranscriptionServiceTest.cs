using System.Net;
using System.Text;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GroupSplit.API.Test.Receipts;

public class ReceiptTranscriptionServiceTest
{
    [Theory]
    [InlineData("receipt.png", "image/png", "image/png")]
    [InlineData("receipt.JPG", "image/jpeg", "image/jpeg")]
    [InlineData("receipt.jpeg", "image/jpeg", "image/jpeg")]
    [InlineData("receipt.jpe", "image/jpeg", "image/jpeg")]
    [InlineData("receipt.jpge", "application/octet-stream", "image/jpeg")]
    [InlineData("receipt.jpg", "image/jpg", "image/jpeg")]
    [InlineData("receipt.webp", "image/webp", "image/webp")]
    [InlineData("receipt.pdf", "application/pdf", "application/pdf")]
    [InlineData("scanner.pdf", "application/octet-stream", "application/pdf")]
    [InlineData("scanner.png", "", "image/png")]
    public async Task Supported_files_and_generic_scanner_types_use_the_canonical_provider_type(
        string name, string type, string expected)
    {
        var provider = new StubProvider(new TranscribedReceipt(1m, 0m, 0m, 1m,
            [new TranscribedReceiptItem("Item", 1m, 1m, 1m, 0m)]));
        await using var content = new MemoryStream([1, 2, 3]);
        var file = new FormFile(content, 0, content.Length, "receipt", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = type
        };
        await new ReceiptTranscriptionService(Mock.Of<IReceiptAttachmentService>(), provider)
            .Transcribe(file, TestContext.Current.CancellationToken);
        Assert.Equal(expected, provider.Document!.ContentType);
        Assert.Equal(name, provider.Document.FileName);
    }

    [Theory]
    [InlineData("receipt.gif", "image/gif")]
    [InlineData("receipt.exe", "image/png")]
    [InlineData("receipt.png", "application/pdf")]
    [InlineData("receipt.jpg", "text/html")]
    public async Task Unsupported_extensions_and_conflicting_types_are_refused_before_the_provider(
        string name, string type)
    {
        var provider = new StubProvider(new TranscribedReceipt(0m, 0m, 0m, 0m, []));
        await using var content = new MemoryStream([1, 2, 3]);
        var file = new FormFile(content, 0, content.Length, "receipt", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = type
        };
        await Assert.ThrowsAsync<ValidationException>(() =>
            new ReceiptTranscriptionService(Mock.Of<IReceiptAttachmentService>(), provider)
                .Transcribe(file, TestContext.Current.CancellationToken));
        Assert.Null(provider.Document);
    }

    [Fact]
    public async Task A_scan_with_rates_but_no_item_taxes_returns_calculated_taxes_before_review()
    {
        var provider = new StubProvider(new TranscribedReceipt(10m, .75m, 0m, 10.75m,
            [new TranscribedReceiptItem("Milk", 4m, 1m, 4m, 0m) { TaxRate = 7.5m },
                new TranscribedReceiptItem("Bread", 3m, 1m, 3m, 0m) { TaxRate = 7.5m },
                new TranscribedReceiptItem("Apples", 3m, 1m, 3m, 0m) { TaxRate = 7.5m }]));
        await using var content = new MemoryStream([1]);
        var file = new FormFile(content, 0, content.Length, "receipt", "receipt.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
        var result = await new ReceiptTranscriptionService(Mock.Of<IReceiptAttachmentService>(), provider)
            .Transcribe(file, TestContext.Current.CancellationToken);
        Assert.Equal([.30m, .22m, .23m], result.Receipt.Items.Select(item => item.TaxAmount));
        Assert.Equal(result.Receipt.Tax, result.Receipt.Items.Sum(item => item.TaxAmount));
    }

    [Fact]
    public async Task It_keeps_storage_and_provider_details_behind_the_transcription_boundary()
    {
        var expenseId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachments = new Mock<IReceiptAttachmentService>(MockBehavior.Strict);
        attachments.Setup(service => service.Download(expenseId, attachmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptAttachmentDownload([1, 2, 3], "image/jpeg", "dinner.jpg"));
        var provider = new StubProvider(new TranscribedReceipt(18m, 1.5m, 3m, 22.5m,
            [new TranscribedReceiptItem("Pizza, large", 18m, 1m, 18m, 1.5m)
                { NormalizedName = "pizza large", Description = "PZA LG" }]));

        var response = await new ReceiptTranscriptionService(attachments.Object, provider)
            .Transcribe(expenseId, attachmentId, TestContext.Current.CancellationToken);

        Assert.Equal(attachmentId, response.AttachmentId);
        Assert.Equal("stub", response.Provider);
        Assert.Equal(22.5m, response.Receipt.Total);
        var item = Assert.Single(response.Receipt.Items);
        Assert.Equal("Pizza, large", item.Name);
        Assert.Equal("pizza large", item.NormalizedName);
        Assert.Equal("PZA LG", item.Description);
        Assert.Null(item.SplitRuleVersionId);
        attachments.VerifyAll();
        Assert.Equal(attachmentId, provider.Document!.AttachmentId);
        Assert.Equal("dinner.jpg", provider.Document.FileName);
    }

    [Fact]
    public async Task It_keeps_receipt_metadata_signed_lines_and_tax_context_in_the_editable_draft()
    {
        var attachmentId = Guid.NewGuid();
        var attachments = new Mock<IReceiptAttachmentService>(MockBehavior.Strict);
        attachments.Setup(service => service.Download(Guid.Empty, attachmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptAttachmentDownload([1], "image/jpeg", "store.jpg"));
        var provider = new StubProvider(new TranscribedReceipt(17.50m, 1.35m, 0m, 18.85m,
            [new TranscribedReceiptItem("Coffee", 20m, 1m, 20m, 1.35m)
            {
                TaxRate = 6.75m, IsTaxable = true
            }, new TranscribedReceiptItem("Loyalty coupon SAVE250", -2.50m, 1m, -2.50m, 0m)])
        {
            MerchantName = "Corner Store",
            PurchaseDate = new DateOnly(2026, 9, 23),
        });

        var response = await new ReceiptTranscriptionService(attachments.Object, provider)
            .Transcribe(Guid.Empty, attachmentId, TestContext.Current.CancellationToken);

        Assert.Equal("Corner Store", response.Receipt.MerchantName);
        Assert.Equal(new DateOnly(2026, 9, 23), response.Receipt.PurchaseDate);
        Assert.Equal(2, response.Receipt.Items.Count);
        var item = response.Receipt.Items[0];
        Assert.Equal(6.75m, item.TaxRate);
        Assert.Equal(true, item.IsTaxable);
        Assert.Equal(-2.50m, response.Receipt.Items[1].TotalPrice);
    }

    [Fact]
    public async Task It_can_read_a_receipt_before_an_expense_exists()
    {
        var provider = new StubProvider(new TranscribedReceipt(18m, 1.5m, 3m, 22.5m,
            [new TranscribedReceiptItem("Pizza, large", 18m, 1m, 18m, 1.5m)]));
        await using var content = new MemoryStream([1, 2, 3]);
        var file = new FormFile(content, 0, content.Length, "receipt", "dinner.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };

        var response = await new ReceiptTranscriptionService(
                Mock.Of<IReceiptAttachmentService>(), provider)
            .Transcribe(file, TestContext.Current.CancellationToken);

        Assert.Equal("stub", response.Provider);
        Assert.Equal(22.5m, response.Receipt.Total);
        Assert.NotEqual(Guid.Empty, provider.Document!.AttachmentId);
        Assert.Equal("dinner.jpg", provider.Document.FileName);
        Assert.Equal("image/jpeg", provider.Document.ContentType);
        Assert.Equal([1, 2, 3], provider.Document.Content);
    }

    private sealed class StubProvider(TranscribedReceipt response) : IReceiptTranscriptionProvider
    {
        public string Name => "stub";
        public ReceiptSourceDocument? Document { get; private set; }

        public Task<TranscribedReceipt> Transcribe(ReceiptSourceDocument document, CancellationToken ct = default)
        {
            Document = document;
            return Task.FromResult(response);
        }
    }

}

using System.Net;
using System.Text;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Veryfi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GroupSplit.API.Test.Receipts;

public class ReceiptTranscriptionServiceTest
{
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
    public async Task It_can_read_a_receipt_that_is_still_attached_to_a_bank_row()
    {
        var bankTransactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachments = new Mock<IReceiptAttachmentService>(MockBehavior.Strict);
        attachments.Setup(service => service.DownloadBank(bankTransactionId, attachmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptAttachmentDownload([4, 5, 6], "application/pdf", "ikea.pdf"));
        var provider = new StubProvider(new TranscribedReceipt(100m, 20m, 0m, 120m,
            [new TranscribedReceiptItem("Shelf", 100m, 1m, 100m, 20m)]));

        var response = await new ReceiptTranscriptionService(attachments.Object, provider)
            .TranscribeBank(bankTransactionId, attachmentId, TestContext.Current.CancellationToken);

        Assert.Equal(attachmentId, response.AttachmentId);
        Assert.Equal(120m, response.Receipt.Total);
        Assert.Equal("ikea.pdf", provider.Document!.FileName);
        attachments.VerifyAll();
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

    [Fact]
    public async Task Veryfi_provider_posts_the_document_with_safe_retry_identity_and_maps_the_draft()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 22.99,
              "tax": 1.50,
              "tip": 3.00,
              "total": 27.49,
              "line_items": [
                { "description": "PZA LG", "normalized_description": "large pizza", "product_info": { "expanded_description": "Pizza, large" }, "price": 9.00, "quantity": 2, "total": 18.00, "tax": 1.50 },
                { "description": "Bread", "price": null, "quantity": 1, "total": 4.99, "tax": 0.00 },
                { "description": "Coupon", "price": null, "quantity": 1, "total": -1.00, "tax": 0.00 },
                { "description": "Free sample", "price": null, "quantity": 1, "total": 0.00, "tax": 0.00 }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);
        var attachmentId = Guid.NewGuid();

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            attachmentId, "dinner.jpg", "image/jpeg", [1, 2, 3]), TestContext.Current.CancellationToken);

        Assert.Equal(27.49m, receipt.Total);
        Assert.Equal(2, receipt.Items.Count);
        var line = receipt.Items[0];
        Assert.Equal("Pizza, large", line.Name);
        Assert.Equal("large pizza", line.NormalizedName);
        Assert.Equal("PZA LG", line.Description);
        Assert.Equal(9m, line.UnitPrice);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(18m, line.TotalPrice);
        Assert.Equal(1.5m, line.TaxAmount);
        var nullPriceLine = receipt.Items[1];
        Assert.Equal("Bread", nullPriceLine.Name);
        Assert.Equal(4.99m, nullPriceLine.UnitPrice);
        Assert.Equal("/api/v8/partner/documents", server.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("client-id", server.Request.Headers.GetValues("Client-Id").Single());
        Assert.Equal("apikey username:api-key", server.Request.Headers.Authorization!.ToString());
        var idempotencyKey = Assert.Single(server.IdempotencyKeys);
        Assert.StartsWith("receipt-transcription:", idempotencyKey);
        Assert.Equal(32, idempotencyKey["receipt-transcription:".Length..].Length);
        Assert.Contains("dinner.jpg", server.Body);
        Assert.Contains($"receipt-attachment:{attachmentId:N}", server.Body);
        Assert.Contains("name=thinking", server.Body);
        Assert.Contains("soft", server.Body);
        Assert.Contains("auto_delete", server.Body);
        Assert.Contains("true", server.Body);
    }

    [Fact]
    public async Task Reprocessing_an_attachment_uses_a_new_idempotency_key()
    {
        var server = new VeryfiServer("""{"line_items": []}""");
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);
        var document = new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1, 2, 3]);

        await provider.Transcribe(document, TestContext.Current.CancellationToken);
        await provider.Transcribe(document, TestContext.Current.CancellationToken);

        Assert.Equal(2, server.IdempotencyKeys.Count);
        Assert.NotEqual(server.IdempotencyKeys[0], server.IdempotencyKeys[1]);
    }

    [Fact]
    public async Task Veryfi_provider_drops_zero_and_negative_rows_and_reconciles_subtotal_and_tax()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 20.00,
              "tax": 2.00,
              "tip": 0.00,
              "total": 22.00,
              "line_items": [
                { "description": "Meal", "price": null, "quantity": 2.0004, "total": 18.01, "tax": null },
                { "description": "Side", "price": null, "quantity": 1, "total": 4.99, "tax": null },
                { "description": "Coupon", "price": null, "quantity": 1, "total": -4.00, "tax": null },
                { "description": "Free item", "price": null, "quantity": 1, "total": 0.00, "tax": null }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1, 2, 3]), TestContext.Current.CancellationToken);

        Assert.Equal(20m, receipt.Items.Sum(item => item.TotalPrice));
        Assert.Equal(2m, receipt.Items.Sum(item => item.TaxAmount));
        Assert.Equal(2, receipt.Items.Count);
        Assert.All(receipt.Items, item =>
        {
            Assert.True(item.TotalPrice > 0m);
            Assert.True(item.UnitPrice > 0m);
            Assert.InRange(item.Quantity, 0.001m, decimal.MaxValue);
            Assert.Equal(item.Quantity, decimal.Round(item.Quantity, 3));
        });
        Assert.Equal("Meal", receipt.Items[0].Name);
        Assert.Equal(2m, receipt.Items[0].Quantity);
        Assert.Equal(15.66m, receipt.Items[0].TotalPrice);
        Assert.Equal(1.57m, receipt.Items[0].TaxAmount);
        Assert.Equal("Side", receipt.Items[1].Name);
        Assert.Equal(4.34m, receipt.Items[1].TotalPrice);
        Assert.Equal(0.43m, receipt.Items[1].TaxAmount);
    }

    /// <summary>
    /// A warehouse receipt: gross line prices, with each instant saving printed as its own row
    /// naming the item number it comes off. Spreading that saving over the whole bill would move
    /// every price on it, so the coupon goes on the one line and the rest stand as printed.
    /// </summary>
    [Fact]
    public async Task A_coupon_that_names_its_line_comes_off_that_line_and_leaves_the_rest_as_printed()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 99.62,
              "tax": 1.95,
              "tip": 0.00,
              "total": 101.57,
              "line_items": [
                { "sku": "91385", "description": "FLAP MEAT", "price": null, "quantity": null, "total": 51.67 },
                { "sku": "782796", "description": "KSWTR40PK", "price": 3.99, "quantity": 2, "total": 7.98 },
                { "sku": "1776788", "description": "JCHS5PCKTPNT", "price": 14.99, "quantity": 2, "total": 29.98, "tax": 1.95 },
                { "sku": "388175", "type": "discount", "text": "388175 / 1776788 4.00-", "total": -4.00 },
                { "sku": "2008132", "description": "ALWAYS FLEX", "price": 17.99, "quantity": 1, "total": 17.99 },
                { "sku": "387813", "type": "discount", "text": "387813 / 2008132 4.00-", "total": -4.00 }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            Guid.NewGuid(), "costco.pdf", "application/pdf", [1, 2, 3]), TestContext.Current.CancellationToken);

        Assert.Equal(4, receipt.Items.Count);
        Assert.Equal(99.62m, receipt.Items.Sum(item => item.TotalPrice));

        // Undiscounted lines keep the figures the till printed, to the cent.
        var meat = receipt.Items[0];
        Assert.Equal(51.67m, meat.TotalPrice);
        Assert.Equal(51.67m, meat.UnitPrice);
        Assert.Equal(1m, meat.Quantity);
        var water = receipt.Items[1];
        Assert.Equal(7.98m, water.TotalPrice);
        Assert.Equal(3.99m, water.UnitPrice);
        Assert.Equal(2m, water.Quantity);

        // Discounted lines lose exactly their own coupon, and the unit price follows the line
        // down: two pairs of trousers at 14.99 less 4.00 are 12.99 each, not 14.99.
        var trousers = receipt.Items[2];
        Assert.Equal(25.98m, trousers.TotalPrice);
        Assert.Equal(12.99m, trousers.UnitPrice);
        Assert.Equal(2m, trousers.Quantity);
        Assert.Equal(1.95m, trousers.TaxAmount);
        var pads = receipt.Items[3];
        Assert.Equal(13.99m, pads.TotalPrice);
        Assert.Equal(13.99m, pads.UnitPrice);

        // The one taxable line keeps all the tax; it is not smeared over the bill.
        Assert.Equal(1.95m, receipt.Tax);
        Assert.Equal(0m, meat.TaxAmount);
    }

    /// <summary>
    /// Costco prints the item number where a quantity would go, so "8 2% MILK 1GAL 2.99" reads
    /// as eight jugs of milk at 37c. Price, quantity and total owe each other a product, and
    /// that is enough to catch it.
    /// </summary>
    [Fact]
    public async Task A_quantity_that_contradicts_the_price_and_total_is_read_again()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 8.98,
              "tax": 0.00,
              "tip": 0.00,
              "total": 8.98,
              "line_items": [
                { "description": "2% MILK 1GAL", "price": 2.99, "quantity": 8, "total": 2.99 },
                { "description": "ROMA TOMATO", "price": 5.99, "quantity": 1344, "total": 5.99 }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            Guid.NewGuid(), "costco.pdf", "application/pdf", [1, 2, 3]), TestContext.Current.CancellationToken);

        Assert.Collection(receipt.Items,
            milk =>
            {
                Assert.Equal(1m, milk.Quantity);
                Assert.Equal(2.99m, milk.UnitPrice);
                Assert.Equal(2.99m, milk.TotalPrice);
            },
            tomatoes =>
            {
                Assert.Equal(1m, tomatoes.Quantity);
                Assert.Equal(5.99m, tomatoes.UnitPrice);
                Assert.Equal(5.99m, tomatoes.TotalPrice);
            });
    }

    /// <summary>
    /// Veryfi names a coupon by type, not only by sign. A discount row reported with a positive
    /// total must not become something the group is asked to divide up and pay for.
    /// </summary>
    [Fact]
    public async Task A_discount_row_is_never_an_item_even_when_its_total_reads_positive()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 9.00,
              "tax": 0.00,
              "tip": 0.00,
              "total": 9.00,
              "line_items": [
                { "description": "Cheese", "price": 10.00, "quantity": 1, "total": 10.00 },
                { "description": "Member saving", "type": "discount", "total": 1.00, "discount": 1.00 },
                { "description": "Card ending 5430", "type": "payment", "total": 9.00 }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            Guid.NewGuid(), "shop.jpg", "image/jpeg", [1, 2, 3]), TestContext.Current.CancellationToken);

        var cheese = Assert.Single(receipt.Items);
        Assert.Equal("Cheese", cheese.Name);
        // The coupon names no line, so it cannot be placed on one: the subtotal is spread
        // instead, and the bill still adds up to what was paid.
        Assert.Equal(9m, cheese.TotalPrice);
        Assert.Equal(9m, receipt.Items.Sum(item => item.TotalPrice));
    }

    /// <summary>
    /// Rows as Veryfi really returned them for a Costco receipt, whose "N @ price" qualifiers
    /// sit in a column of their own and get read onto whichever row they land beside. It glues
    /// one onto the mozzarella, which never had a quantity, and another onto a coupon row,
    /// which comes back with a positive total for a saving of 2.70.
    /// </summary>
    [Fact]
    public async Task A_qualifier_read_onto_the_wrong_row_costs_neither_a_price_nor_a_coupon()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 47.74,
              "tax": 0.00,
              "tip": null,
              "total": 47.74,
              "discount": 6.70,
              "line_items": [
                { "type": "food", "sku": "1189000", "description": "MOZZARELLA", "quantity": 2.0, "price": 3.99, "total": 7.79 },
                { "type": "product", "sku": "782796", "description": "***KSWTR40PK", "quantity": 1.0, "price": null, "total": 7.98 },
                { "type": "food", "sku": "2062082", "description": "TERIYAKIUDON", "quantity": 1.0, "price": null, "total": 8.69 },
                { "type": null, "sku": "388233/2062082", "description": null, "quantity": 2.0, "price": 14.99, "total": 27.28, "discount": -2.70 },
                { "type": "food", "sku": "1776788", "description": "JCHS5PCKTPNT", "quantity": 1.0, "price": null, "total": 29.98 },
                { "type": null, "sku": "388175/1776788", "description": null, "quantity": 1.0, "price": null, "total": -4.00, "discount": -4.00 }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            Guid.NewGuid(), "costco.pdf", "application/pdf", [1, 2, 3]), TestContext.Current.CancellationToken);

        // The 27.28 row is a coupon, not a purchase: nothing bought is named on it, and it
        // takes 2.70 off. It must not arrive as a thing the group is asked to pay for.
        Assert.Equal(4, receipt.Items.Count);
        Assert.DoesNotContain(receipt.Items, item => item.TotalPrice == 27.28m);
        Assert.Equal(47.74m, receipt.Items.Sum(item => item.TotalPrice));

        // A quantity belonging to another line does not turn one block of mozzarella into
        // 1.952 of them. The printed total stands, as one of what it is.
        var mozzarella = receipt.Items[0];
        Assert.Equal(1m, mozzarella.Quantity);
        Assert.Equal(7.79m, mozzarella.UnitPrice);
        Assert.Equal(7.79m, mozzarella.TotalPrice);

        // Both coupons land on the lines they name, and everything else stands as printed.
        Assert.Equal(7.98m, receipt.Items[1].TotalPrice);
        Assert.Equal(5.99m, receipt.Items[2].TotalPrice);
        Assert.Equal(25.98m, receipt.Items[3].TotalPrice);
    }

    /// <summary>
    /// A line whose name wrapped onto the next row of the paper comes back with nothing but its
    /// item number and its price. The money is real, so the line has to stay on the bill under
    /// a name somebody can match against the receipt and correct.
    /// </summary>
    [Fact]
    public async Task A_line_with_no_name_of_its_own_is_named_rather_than_dropped()
    {
        var server = new VeryfiServer("""
            {
              "subtotal": 12.28,
              "tax": 0.00,
              "tip": null,
              "total": 12.28,
              "line_items": [
                { "type": "food", "sku": "568915", "description": null, "text": "568915\t\t5.29 N", "quantity": 1.0, "total": 5.29 },
                { "type": "food", "sku": "1189000", "description": null, "text": "1189000 MOZZARELLA\t6.99 N", "quantity": 1.0, "total": 6.99 }
              ]
            }
            """);
        using var client = new HttpClient(server) { BaseAddress = new Uri("https://veryfi.test/") };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(new ReceiptSourceDocument(
            Guid.NewGuid(), "costco.pdf", "application/pdf", [1, 2, 3]), TestContext.Current.CancellationToken);

        Assert.Equal(2, receipt.Items.Count);
        // Nothing but numbers on the row, so it is called after the number on the paper --
        // never "568915\t\t5.29 N", and never quietly dropped along with its 5.29.
        Assert.Equal("Item 568915", receipt.Items[0].Name);
        Assert.Equal(5.29m, receipt.Items[0].TotalPrice);
        // Where the raw text does carry a name, the item number and the price come off it.
        Assert.Equal("MOZZARELLA", receipt.Items[1].Name);
        Assert.Equal(6.99m, receipt.Items[1].TotalPrice);
    }

    [Fact]
    public async Task A_provider_refusal_is_a_retryable_bad_gateway()
    {
        using var client = new HttpClient(new VeryfiServer("{}", HttpStatusCode.ServiceUnavailable))
        {
            BaseAddress = new Uri("https://veryfi.test/")
        };
        var provider = new VeryfiReceiptTranscriptionProvider(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1]), TestContext.Current.CancellationToken));

        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);
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

    private sealed class VeryfiServer(string response, HttpStatusCode status = HttpStatusCode.Created) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public List<string> IdempotencyKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            IdempotencyKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}

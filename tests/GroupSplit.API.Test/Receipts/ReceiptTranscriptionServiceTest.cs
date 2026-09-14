using System.Net;
using System.Text;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Veryfi;
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

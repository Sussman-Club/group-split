using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// The private source-file routes for both sides of the receipt flow: an expense and an
/// imported bank row waiting to be filed.
/// </summary>
public class ReceiptAttachmentEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] StoredFile = [1, 2, 3, 4];

    private readonly Mock<IAmazonS3> _storage = new(MockBehavior.Strict);
    private readonly Mock<IReceiptTranscriptionService> _transcription = new(MockBehavior.Strict);
    private ApiEndpointHost _host = null!;

    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _storage
            .Setup(storage => storage.PutObjectAsync(
                It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());
        _storage
            .Setup(storage => storage.GetObjectAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(StoredFile.ToArray())
            });
        _storage
            .Setup(storage => storage.DeleteObjectAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        _transcription
            .Setup(service => service.Transcribe(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid attachmentId, CancellationToken _) =>
                new ReceiptTranscriptionResponse(attachmentId, "stub", new SaveReceiptRequest
                {
                    Subtotal = 4m,
                    Total = 4m,
                    Items = [new ReceiptItemInput
                    {
                        Name = "Receipt item",
                        UnitPrice = 4m,
                        Quantity = 1m,
                        TotalPrice = 4m
                    }]
                }));
        _transcription
            .Setup(service => service.TranscribeBank(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid attachmentId, CancellationToken _) =>
                new ReceiptTranscriptionResponse(attachmentId, "stub", new SaveReceiptRequest
                {
                    Subtotal = 4m,
                    Total = 4m,
                    Items = [new ReceiptItemInput
                    {
                        Name = "Bank receipt item",
                        UnitPrice = 4m,
                        Quantity = 1m,
                        TotalPrice = 4m
                    }]
                }));

        _host = await ApiEndpointHost.StartAsync(services =>
        {
            services.AddSingleton(_storage.Object);
            services.AddSingleton(_transcription.Object);
            services.Configure<ReceiptStorageOptions>(options => options.BucketName = "receipts-test");
        });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_expense_attachment_can_be_uploaded_listed_downloaded_transcribed_and_deleted()
    {
        var expenseId = await CreateExpense();

        var upload = await Client.PostAsync(
            $"/transactions/{expenseId}/receipt-attachments", FileContent("dinner.jpg", "image/jpeg"), Ct);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var attachment = (await upload.Content.ReadFromJsonAsync<ReceiptAttachmentResponse>(Json, Ct))!;
        Assert.Equal(expenseId, attachment.ExpenseId);
        Assert.Equal("dinner.jpg", attachment.FileName);
        Assert.Equal("image/jpeg", attachment.ContentType);
        Assert.Equal(StoredFile.Length, attachment.Length);
        Assert.Null(attachment.ReceiptId);

        var listed = await Client.GetFromJsonAsync<List<ReceiptAttachmentResponse>>(
            $"/transactions/{expenseId}/receipt-attachments", Json, Ct);
        Assert.Equal(attachment, Assert.Single(listed!));

        var downloaded = await Client.GetAsync(
            $"/transactions/{expenseId}/receipt-attachments/{attachment.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("image/jpeg", downloaded.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StoredFile, await downloaded.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("dinner.jpg", downloaded.Content.Headers.ContentDisposition?.FileNameStar ?? "");

        var preview = await Client.GetAsync(
            $"/transactions/{expenseId}/receipt-attachments/{attachment.Id}?inline=true", Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StoredFile, await preview.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("inline", preview.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("dinner.jpg", preview.Content.Headers.ContentDisposition?.FileNameStar ?? "");

        var transcribed = await Client.PostAsync(
            $"/transactions/{expenseId}/receipt-attachments/{attachment.Id}/transcribe", null, Ct);
        Assert.Equal(HttpStatusCode.OK, transcribed.StatusCode);
        var draft = (await transcribed.Content.ReadFromJsonAsync<ReceiptTranscriptionResponse>(Json, Ct))!;
        Assert.Equal(attachment.Id, draft.AttachmentId);
        Assert.Equal("Receipt item", Assert.Single(draft.Receipt.Items).Name);

        var deleted = await Client.DeleteAsync(
            $"/transactions/{expenseId}/receipt-attachments/{attachment.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await Client.GetFromJsonAsync<List<ReceiptAttachmentResponse>>(
            $"/transactions/{expenseId}/receipt-attachments", Json, Ct);
        Assert.Empty(afterDelete!);
        _storage.Verify(storage => storage.PutObjectAsync(
            It.Is<PutObjectRequest>(request => request.BucketName == "receipts-test"
                && request.Key.StartsWith($"{expenseId:N}/", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(storage => storage.GetObjectAsync(
            "receipts-test",
            It.Is<string>(key => key.StartsWith($"{expenseId:N}/", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _storage.Verify(storage => storage.DeleteObjectAsync(
            "receipts-test", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _transcription.Verify(service => service.Transcribe(
            expenseId, attachment.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_bank_attachment_can_be_downloaded_and_deleted_before_filing()
    {
        var row = await CreateBankRow();

        var upload = await Client.PostAsync(
            $"/inbox/{row.Id}/receipt-attachments", FileContent("statement.pdf", "application/pdf"), Ct);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var attachment = (await upload.Content.ReadFromJsonAsync<ReceiptAttachmentResponse>(Json, Ct))!;
        Assert.Equal(row.Id, attachment.BankTransactionId);
        Assert.Null(attachment.ExpenseId);

        var listed = await Client.GetFromJsonAsync<List<ReceiptAttachmentResponse>>(
            $"/inbox/{row.Id}/receipt-attachments", Json, Ct);
        Assert.Equal(attachment, Assert.Single(listed!));

        var downloaded = await Client.GetAsync(
            $"/inbox/{row.Id}/receipt-attachments/{attachment.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("application/pdf", downloaded.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StoredFile, await downloaded.Content.ReadAsByteArrayAsync(Ct));

        _storage.Verify(storage => storage.GetObjectAsync(
            "receipts-test",
            It.Is<string>(key => key.StartsWith($"{row.Id:N}/", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);

        var transcribed = await Client.PostAsync(
            $"/inbox/{row.Id}/receipt-attachments/{attachment.Id}/transcribe", null, Ct);
        Assert.Equal(HttpStatusCode.OK, transcribed.StatusCode);
        var draft = (await transcribed.Content.ReadFromJsonAsync<ReceiptTranscriptionResponse>(Json, Ct))!;
        Assert.Equal(attachment.Id, draft.AttachmentId);
        Assert.Equal("Bank receipt item", Assert.Single(draft.Receipt.Items).Name);

        var deleted = await Client.DeleteAsync(
            $"/inbox/{row.Id}/receipt-attachments/{attachment.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await Client.GetFromJsonAsync<List<ReceiptAttachmentResponse>>(
            $"/inbox/{row.Id}/receipt-attachments", Json, Ct);
        Assert.Empty(afterDelete!);
        _transcription.Verify(service => service.TranscribeBank(
            row.Id, attachment.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_bank_attachment_moves_to_the_expense_when_the_row_is_linked()
    {
        var row = await CreateBankRow();
        var expenseId = await CreateExpense();

        var upload = await Client.PostAsync(
            $"/inbox/{row.Id}/receipt-attachments", FileContent("pending.png", "image/png"), Ct);
        var attachment = (await upload.Content.ReadFromJsonAsync<ReceiptAttachmentResponse>(Json, Ct))!;

        var linked = await Client.PostAsJsonAsync($"/inbox/{row.Id}/link",
            new LinkBankTransactionRequest { TransactionId = expenseId }, Json, Ct);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        var inboxFiles = await Client.GetFromJsonAsync<List<ReceiptAttachmentResponse>>(
            $"/inbox/{row.Id}/receipt-attachments", Json, Ct);
        Assert.Empty(inboxFiles!);

        var expenseFiles = await Client.GetFromJsonAsync<List<ReceiptAttachmentResponse>>(
            $"/transactions/{expenseId}/receipt-attachments", Json, Ct);
        var moved = Assert.Single(expenseFiles!);
        Assert.Equal(attachment.Id, moved.Id);
        Assert.Equal(expenseId, moved.ExpenseId);
        Assert.Equal(row.Id, moved.BankTransactionId);
    }

    [Fact]
    public async Task Deleting_an_expense_removes_all_receipt_objects_and_metadata()
    {
        var expenseId = await CreateExpense();

        var upload = await Client.PostAsync(
            $"/transactions/{expenseId}/receipt-attachments", FileContent("dinner.jpg", "image/jpeg"), Ct);
        upload.EnsureSuccessStatusCode();
        var attachment = (await upload.Content.ReadFromJsonAsync<ReceiptAttachmentResponse>(Json, Ct))!;

        var secondUpload = await Client.PostAsync(
            $"/transactions/{expenseId}/receipt-attachments", FileContent("dinner.pdf", "application/pdf"), Ct);
        secondUpload.EnsureSuccessStatusCode();
        var secondAttachment =
            (await secondUpload.Content.ReadFromJsonAsync<ReceiptAttachmentResponse>(Json, Ct))!;

        var deleted = await Client.DeleteAsync($"/transactions/{expenseId}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.Set<ReceiptAttachment>()
                .Where(candidate => candidate.Id == attachment.Id || candidate.Id == secondAttachment.Id)
                .ToListAsync(Ct));
        }

        _storage.Verify(storage => storage.DeleteObjectAsync(
            "receipts-test",
            It.Is<string>(key => key.StartsWith($"{expenseId:N}/", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private async Task<Guid> CreateExpense(decimal amount = 10m)
    {
        var response = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow
        }, Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("id").GetGuid();
    }

    private async Task<BankTransaction> CreateBankRow()
    {
        // Any authenticated request provisions the host user used by VisibleBank.
        await Client.GetAsync("/bank-connections", Ct);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Set<Data.Entities.User>().SingleAsync(Ct);
        var connection = new BankConnection
        {
            User = user,
            Provider = "test",
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Test bank",
            AccessTokenCiphertext = "not-used",
            LinkedAt = DateTimeOffset.UtcNow
        };
        connection.Accounts.Add(new LinkedAccount
        {
            ProviderAccountId = $"account-{Guid.NewGuid():N}",
            Name = "Checking",
            Type = "depository"
        });
        db.Add(connection);
        await db.SaveChangesAsync(Ct);

        var row = new BankTransaction
        {
            Account = connection.Accounts.Single(),
            ProviderTransactionId = $"transaction-{Guid.NewGuid():N}",
            Date = new DateOnly(2026, 9, 15),
            Amount = 10m,
            Description = "TEST PURCHASE",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };
        db.Add(row);
        await db.SaveChangesAsync(Ct);
        return row;
    }

    private static MultipartFormDataContent FileContent(string name, string contentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(StoredFile);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", name);
        return content;
    }
}

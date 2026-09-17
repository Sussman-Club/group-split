using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

[Collection(EnvironmentCollection.Name)]
public sealed class ReceiptCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();
    private static readonly Guid Expense = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();
    public ReceiptCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }
    public void Dispose() { _api.Dispose(); _environment.Dispose(); }

    [Fact]
    public async Task Set_sends_rule_version_and_tax_on_each_item()
    {
        _api.Returns($"/api/transactions/{Expense}/receipt", Bill());
        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(), "--item", $"Food=100/tax6@{Version}");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var body = _api.Requests.Last().Json;
        Assert.Equal(Version, body.GetProperty("items")[0].GetProperty("splitRuleVersionId").GetGuid());
        Assert.Equal(6m, body.GetProperty("items")[0].GetProperty("taxAmount").GetDecimal());
    }

    [Fact]
    public async Task Set_sends_the_stored_line_id_when_correcting_a_line()
    {
        var itemId = Guid.NewGuid();
        _api.Returns($"/api/transactions/{Expense}/receipt", Bill(new
        {
            id = itemId,
            name = "Kirkland Signature Water 40 Pack",
            normalizedName = "kirkland signature water 40 pack",
            description = "KIRKLAND SIGNATURE WATER 40 PK",
            unitPrice = 3.99m,
            quantity = 2m,
            totalPrice = 7.98m,
            taxAmount = 0m,
            splitRuleVersionId = (Guid?)null,
            splitRuleName = (string?)null,
            splitRule = (object?)null
        }));

        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(),
            "--item", $"{itemId}#Kirkland Signature Water=7.98x2");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var sent = _api.Requests.Single(request => request.Method == "PUT").Json;
        Assert.Equal(itemId, sent.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Single(_api.Requests);
    }
    [Fact]
    public async Task Rule_command_updates_one_item()
    {
        var item = Guid.NewGuid();
        _api.Returns($"/api/transactions/{Expense}/receipt/items/{item}/rule", Bill());
        var result = await Cli.RunAsync("receipts", "rule", Expense.ToString(), item.ToString(), "--rule-version", Version.ToString());
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(Version, _api.Requests.Last().Json.GetProperty("splitRuleVersionId").GetGuid());
    }
    [Fact]
    public void Parser_keeps_item_identity_when_correcting_a_line()
    {
        var id = Guid.NewGuid();
        var item = ReceiptItems.Parse("--item", [$"{id}#Food=20x2/tax1@{Version}"]).Single();
        Assert.Equal(id, item.Id); Assert.Equal(Version, item.SplitRuleVersionId);
        Assert.Equal(2, item.Quantity); Assert.Equal(20m, item.TotalPrice); Assert.Equal(10m, item.UnitPrice);
        Assert.Equal(1m, item.TaxAmount);
    }
    [Theory]
    [InlineData("Food=10@not-a-version")]
    [InlineData("Food=10/tax")]
    public async Task Malformed_item_is_refused_before_a_request(string value)
    {
        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(), "--item", value);
        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode); Assert.Empty(_api.Requests);
    }
    [Fact]
    public async Task Removed_bank_split_command_is_not_available()
    {
        var result = await Cli.RunAsync("receipts", "split", Expense.ToString());
        Assert.NotEqual(ExitCodes.Success, result.ExitCode); Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Draft_transcription_sends_the_receipt_file_without_saving_an_expense_receipt()
    {
        var path = TempReceipt("draft.pdf");
        try
        {
            _api.Returns("/api/receipts/transcribe", Transcription());

            var result = await Cli.RunAsync("receipts", "transcribe", path);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            var request = _api.Requests.Single();
            Assert.Equal("POST", request.Method);
            Assert.Equal("/api/receipts/transcribe", request.Path);
            Assert.Contains("draft.pdf", request.Body);
            Assert.Contains("receipt", result.Stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Expense_receipt_attachment_can_be_uploaded_and_listed()
    {
        var attachment = Guid.NewGuid();
        var path = TempReceipt("expense.png");
        try
        {
            _api.Returns($"/api/transactions/{Expense}/receipt-attachments", Attachment(attachment), method: "POST");
            _api.Returns($"/api/transactions/{Expense}/receipt-attachments", new[] { Attachment(attachment) }, method: "GET");

            var uploaded = await Cli.RunAsync(
                "receipts", "attachments", "upload", Expense.ToString(), path);
            var listed = await Cli.RunAsync(
                "receipts", "attachments", "list", Expense.ToString());

            Assert.Equal(ExitCodes.Success, uploaded.ExitCode);
            Assert.Equal(ExitCodes.Success, listed.ExitCode);
            Assert.Contains("expense.png", _api.Requests.Single(request => request.Method == "POST").Body);
            Assert.Equal(attachment, listed.Json[0].GetProperty("id").GetGuid());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Expense_attachment_transcription_is_a_separate_step()
    {
        var attachment = Guid.NewGuid();
        _api.Returns(
            $"/api/transactions/{Expense}/receipt-attachments/{attachment}/transcribe",
            Transcription(attachment));

        var result = await Cli.RunAsync(
            "receipts", "attachments", "transcribe", Expense.ToString(), attachment.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("POST", _api.Requests.Single().Method);
        Assert.Contains("receipt", result.Stdout);
    }

    [Fact]
    public async Task Expense_receipt_attachment_download_writes_the_bytes_to_a_new_file()
    {
        var attachment = Guid.NewGuid();
        var destination = Path.Combine(Path.GetTempPath(), $"groupsplit-{Guid.NewGuid():N}.pdf");
        var bytes = new byte[] { 1, 2, 3, 4 };
        try
        {
            _api.ReturnsBytes($"/api/transactions/{Expense}/receipt-attachments/{attachment}", bytes);

            var result = await Cli.RunAsync(
                "receipts", "attachments", "download", Expense.ToString(), attachment.ToString(), destination);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Equal(bytes, File.ReadAllBytes(destination));
            Assert.Equal("GET", _api.Requests.Single().Method);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task Deleting_an_expense_attachment_is_confirmation_gated()
    {
        var attachment = Guid.NewGuid();
        _api.Returns($"/api/transactions/{Expense}/receipt-attachments", new[] { Attachment(attachment) }, method: "GET");
        _api.NoContent($"/api/transactions/{Expense}/receipt-attachments/{attachment}", method: "DELETE");

        var confirmation = await Cli.RunAsync(
            "receipts", "attachments", "delete", Expense.ToString(), attachment.ToString());
        Assert.Equal(0, _api.Requests.Count(request => request.Method == "DELETE"));

        var deleted = await Cli.RunAsync(
            "receipts", "attachments", "delete", Expense.ToString(), attachment.ToString(), "--yes");

        Assert.Equal(ExitCodes.ConfirmationRequired, confirmation.ExitCode);
        Assert.Equal(ExitCodes.Success, deleted.ExitCode);
        Assert.Contains(_api.Requests, request => request.Method == "DELETE");
    }

    private static string TempReceipt(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"groupsplit-{Guid.NewGuid():N}-{fileName}");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static object Attachment(Guid id) => new
    {
        id,
        expenseId = Expense,
        bankTransactionId = (Guid?)null,
        receiptId = (Guid?)null,
        fileName = "receipt.pdf",
        contentType = "application/pdf",
        length = 3,
        uploadedAt = DateTimeOffset.UtcNow
    };

    private static object Transcription(Guid? attachmentId = null) => new
    {
        attachmentId = attachmentId ?? Guid.NewGuid(),
        provider = "test-provider",
        receipt = new
        {
            subtotal = 10m,
            tax = 1m,
            tip = 0m,
            total = 11m,
            items = new[]
            {
                new
                {
                    id = (Guid?)null,
                    name = "Coffee",
                    normalizedName = "coffee",
                    description = (string?)null,
                    unitPrice = 10m,
                    quantity = 1m,
                    totalPrice = 10m,
                    taxAmount = 1m,
                    splitRuleVersionId = (Guid?)null
                }
            }
        }
    };

    private static object Bill(params object[] items) => new { id = Guid.NewGuid(), expenseId = Expense, subtotal = 100m,
        tax = 6m, tip = 0m, total = 106m, missingRuleItemCount = 0, canDivide = true, dividesItsExpense = true,
        canEdit = true, items };
}

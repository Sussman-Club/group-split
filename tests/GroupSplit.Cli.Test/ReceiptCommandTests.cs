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
        // The saved bill is read first, for what the command has no flag for, and the line's
        // source text is still left to the server to keep: it is not sent back.
        Assert.Equal(["GET", "PUT"], _api.Requests.Select(request => request.Method));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("items")[0].GetProperty("description").ValueKind);
    }

    /// <summary>
    /// Correcting one price keeps what the command cannot say: where and when the bill was
    /// read, every line's tax and rate, the reading of a name that did not change, and the
    /// tip. It used to send none of them, and the save wrote each as nothing.
    /// </summary>
    [Fact]
    public async Task Set_keeps_the_saved_bills_metadata_it_does_not_mention()
    {
        var water = Guid.NewGuid();
        var soap = Guid.NewGuid();
        _api.Returns($"/api/transactions/{Expense}/receipt", Saved(3m,
            Line(water, "Water", "kirkland water 40 pk", 8.00m, tax: .48m, rate: 6m, taxable: true),
            Line(soap, "Soap", "dial soap", 4.00m, tax: 0m, rate: null, taxable: false)), method: "GET");
        _api.Returns($"/api/transactions/{Expense}/receipt", Bill(), method: "PUT");

        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(),
            "--item", $"{water}#Water=8.00", "--item", $"{soap}#Hand soap=4.50");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var sent = _api.Requests.Single(request => request.Method == "PUT").Json;
        Assert.Equal("Costco", sent.GetProperty("merchantName").GetString());
        Assert.Equal("2026-09-30", sent.GetProperty("purchaseDate").GetString());
        Assert.Equal(3m, sent.GetProperty("tip").GetDecimal());
        Assert.Equal(.48m, sent.GetProperty("tax").GetDecimal());
        Assert.Equal(15.98m, sent.GetProperty("total").GetDecimal());

        var kept = sent.GetProperty("items")[0];
        Assert.Equal("kirkland water 40 pk", kept.GetProperty("normalizedName").GetString());
        Assert.Equal(.48m, kept.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(6m, kept.GetProperty("taxRate").GetDecimal());
        Assert.True(kept.GetProperty("isTaxable").GetBoolean());

        // Renamed, so the old reading of its name no longer describes it.
        var renamed = sent.GetProperty("items")[1];
        Assert.Equal(JsonValueKind.Null, renamed.GetProperty("normalizedName").ValueKind);
        Assert.False(renamed.GetProperty("isTaxable").GetBoolean());
    }

    /// <summary>
    /// A line that states its tax is taken at its word: the saved rate would otherwise work
    /// its own figure out over the top of the one typed.
    /// </summary>
    [Fact]
    public async Task Set_takes_a_stated_tax_over_the_saved_rate()
    {
        var water = Guid.NewGuid();
        _api.Returns($"/api/transactions/{Expense}/receipt", Saved(0m,
            Line(water, "Water", "water", 8.00m, tax: .48m, rate: 6m, taxable: true)), method: "GET");
        _api.Returns($"/api/transactions/{Expense}/receipt", Bill(), method: "PUT");

        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(),
            "--item", $"{water}#Water=8.00/tax0.50");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var line = _api.Requests.Single(request => request.Method == "PUT").Json.GetProperty("items")[0];
        Assert.Equal(.50m, line.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("taxRate").ValueKind);
    }

    /// <summary>A first bill has nothing saved to keep, and is written as typed.</summary>
    [Fact]
    public async Task Set_on_an_expense_with_no_bill_writes_it_as_typed()
    {
        _api.Problem($"/api/transactions/{Expense}/receipt", 404, "RECEIPT_NOT_FOUND",
            "This expense has no itemized bill.", method: "GET");
        _api.Returns($"/api/transactions/{Expense}/receipt", Bill(), method: "PUT");

        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(), "--item", "Food=100/tax6");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var sent = _api.Requests.Single(request => request.Method == "PUT").Json;
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("merchantName").ValueKind);
        Assert.Equal(6m, sent.GetProperty("tax").GetDecimal());
        Assert.Equal(0m, sent.GetProperty("tip").GetDecimal());
        Assert.Equal(106m, sent.GetProperty("total").GetDecimal());
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

    /// <summary>A saved bill read off a Costco receipt, with its tip and its lines.</summary>
    private static object Saved(decimal tip, params object[] items) => new
    {
        id = Guid.NewGuid(), expenseId = Expense, subtotal = 12m, tax = .48m, tip, total = 12.48m + tip,
        missingRuleItemCount = 0, canDivide = true, dividesItsExpense = true, canEdit = true,
        merchantName = "Costco", purchaseDate = "2026-09-30", items
    };

    private static object Line(Guid id, string name, string normalizedName, decimal price, decimal tax,
        decimal? rate, bool? taxable) => new
    {
        id, name, normalizedName, description = "AS PRINTED", unitPrice = price, quantity = 1m,
        totalPrice = price, taxAmount = tax, taxRate = rate, isTaxable = taxable,
        splitRuleVersionId = (Guid?)null, splitRuleName = (string?)null, splitRule = (object?)null
    };

    private static object Bill(params object[] items) => new { id = Guid.NewGuid(), expenseId = Expense, subtotal = 100m,
        tax = 6m, tip = 0m, total = 106m, missingRuleItemCount = 0, canDivide = true, dividesItsExpense = true,
        canEdit = true, items };
}

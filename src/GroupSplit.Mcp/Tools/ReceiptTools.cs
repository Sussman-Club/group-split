using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ReceiptTools
{
    [McpServerTool(Name = "get_receipt", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get an expense's saved itemized receipt.")]
    public static Task<ReceiptResponse> GetReceipt(ReceiptsClient client, Guid expenseId, CancellationToken ct = default) =>
        client.GetReceiptAsync(expenseId, ct);

    [McpServerTool(Name = "list_receipt_inbox", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your pending receipt inbox items.")]
    public static Task<ICollection<ReceiptInboxResponse>> ListReceiptInbox(ReceiptsClient client, CancellationToken ct = default) =>
        client.ListReceiptInboxAsync(ct);

    [McpServerTool(Name = "get_receipt_inbox_item", ReadOnly = true, Destructive = false, Idempotent = true), Description("Inspect one of your receipt inbox items.")]
    public static Task<ReceiptInboxResponse> GetReceiptInboxItem(ReceiptsClient client, Guid receiptId, CancellationToken ct = default) =>
        client.GetReceiptInboxAsync(receiptId, ct);

    [McpServerTool(Name = "receipt_inbox_matches", ReadOnly = true, Destructive = false, Idempotent = true), Description("Find possible expenses matching a receipt inbox item.")]
    public static Task<ICollection<ReceiptExpenseCandidate>> ReceiptInboxMatches(ReceiptsClient client, Guid receiptId, CancellationToken ct = default) =>
        client.MatchReceiptInboxAsync(receiptId, ct);

    [McpServerTool(Name = "preview_receipt_division", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview the division of an expense by its itemized receipt without saving changes.")]
    public static Task<ReceiptDivisionResponse> PreviewReceiptDivision(ReceiptsClient client, Guid expenseId, CancellationToken ct = default) =>
        client.PreviewReceiptDivisionAsync(expenseId, ct);

    [McpServerTool(Name = "expense_receipts", ReadOnly = true, Destructive = false, Idempotent = true), Description("List receipt attachments for an expense you can access.")]
    public static Task<ICollection<ReceiptAttachmentResponse>> ExpenseReceipts(ReceiptsClient client, Guid expenseId, CancellationToken ct = default) =>
        client.GetReceiptAttachmentsAsync(expenseId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "upload_expense_receipt", ReadOnly = false, Destructive = false, Idempotent = false), Description("Attach a JPEG, PNG, WebP or PDF receipt to an expense. Supply the file bytes as base64, not a local path or URL. Maximum file size is 10 MiB.")]
    public static async Task<ReceiptAttachmentResponse> UploadExpenseReceipt(ReceiptsClient client, Guid expenseId, string fileName, string contentBase64, CancellationToken ct = default)
    {
        var contentType = ReceiptFileTypes.Resolve(fileName, null) ?? throw new ArgumentException("The receipt must be a JPEG, PNG, WebP or PDF file.", nameof(fileName));
        var bytes = DecodeReceipt(contentBase64);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await client.UploadReceiptAttachmentAsync(expenseId, new FileParameter(stream, Path.GetFileName(fileName), contentType), ct);
    }

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "upload_inbox_receipt", ReadOnly = false, Destructive = false, Idempotent = false), Description("Attach a JPEG, PNG, WebP or PDF file to a receipt inbox item. Supply file bytes as base64, not a local path or URL. Maximum file size is 10 MiB.")]
    public static async Task<ReceiptAttachmentResponse> UploadInboxReceipt(ReceiptsClient client, Guid receiptId, string fileName, string contentBase64, CancellationToken ct = default)
    {
        var contentType = ReceiptFileTypes.Resolve(fileName, null) ?? throw new ArgumentException("The receipt must be a JPEG, PNG, WebP or PDF file.", nameof(fileName));
        var bytes = DecodeReceipt(contentBase64);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await client.UploadInboxReceiptFileAsync(receiptId, new FileParameter(stream, Path.GetFileName(fileName), contentType), ct);
    }

    [McpServerTool(Name = "download_expense_receipt", ReadOnly = true, Destructive = false, Idempotent = true), Description("Read an accessible expense receipt attachment as base64 bytes and its media type. Maximum file size is 10 MiB.")]
    public static Task<ReceiptFileContent> DownloadExpenseReceipt(IHttpClientFactory clients, Guid expenseId, Guid attachmentId, CancellationToken ct = default) =>
        DownloadReceipt(clients, $"transactions/{expenseId:D}/receipt-attachments/{attachmentId:D}", ct);

    [McpServerTool(Name = "download_inbox_receipt", ReadOnly = true, Destructive = false, Idempotent = true), Description("Read one of your receipt inbox attachments as base64 bytes and its media type. Maximum file size is 10 MiB.")]
    public static Task<ReceiptFileContent> DownloadInboxReceipt(IHttpClientFactory clients, Guid receiptId, Guid attachmentId, CancellationToken ct = default) =>
        DownloadReceipt(clients, $"receipts/inbox/{receiptId:D}/files/{attachmentId:D}", ct);

    [McpServerTool(Name = "transcribe_expense_receipt", ReadOnly = true, Destructive = false, Idempotent = true), Description("Extract draft receipt details from an existing expense attachment without saving the draft.")]
    public static Task<ReceiptTranscriptionResponse> TranscribeExpenseReceipt(ReceiptsClient client, Guid expenseId, Guid attachmentId, CancellationToken ct = default) =>
        client.TranscribeReceiptAttachmentAsync(expenseId, attachmentId, ct);

    [McpServerTool(Name = "transcribe_inbox_receipt", ReadOnly = true, Destructive = false, Idempotent = true), Description("Extract draft receipt details from an inbox attachment without saving the draft.")]
    public static Task<ReceiptDraftResponse> TranscribeInboxReceipt(ReceiptsClient client, Guid receiptId, Guid attachmentId, CancellationToken ct = default) =>
        client.TranscribeInboxReceiptAsync(receiptId, attachmentId, ct);

    [McpServerTool(Name = "transcribe_receipt_file", ReadOnly = true, Destructive = false, Idempotent = false), Description("Extract a draft receipt from a JPEG, PNG, WebP or PDF supplied as base64 bytes without saving it. The file is sent to the configured transcription service; maximum size is 10 MiB.")]
    public static async Task<ReceiptDraftResponse> TranscribeReceiptFile(ReceiptsClient client, string fileName, string contentBase64, CancellationToken ct = default)
    {
        var contentType = ReceiptFileTypes.Resolve(fileName, null) ?? throw new ArgumentException("The receipt must be a JPEG, PNG, WebP or PDF file.", nameof(fileName));
        await using var stream = new MemoryStream(DecodeReceipt(contentBase64), writable: false);
        return await client.TranscribeReceiptDraftAsync(new FileParameter(stream, Path.GetFileName(fileName), contentType), ct);
    }

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "save_receipt", ReadOnly = false, Destructive = true, Idempotent = false), Description("Replace an expense's complete itemized receipt after user confirmation. This may recalculate shares and change group balances.")]
    public static Task<ReceiptResponse> SaveReceipt(ReceiptsClient client, Guid expenseId, SaveReceiptRequest receipt, CancellationToken ct = default) =>
        client.SaveReceiptAsync(expenseId, receipt, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_receipt_item_rule", ReadOnly = false, Destructive = true, Idempotent = false), Description("Set or clear an itemized receipt line's split rule after user confirmation. This may recalculate shares and change group balances.")]
    public static Task<ReceiptResponse> SetReceiptItemRule(ReceiptsClient client, Guid expenseId, Guid itemId, Guid? splitRuleVersionId = null, CancellationToken ct = default) =>
        client.SetReceiptItemRuleAsync(expenseId, itemId, new SetReceiptItemRuleRequest { SplitRuleVersionId = splitRuleVersionId }, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "update_receipt_item", ReadOnly = false, Destructive = true, Idempotent = false), Description("Change only specified fields of a receipt item after user confirmation. Changes may recalculate expense shares and group balances. Use clear flags to remove optional fields.")]
    public static Task<ReceiptResponse> UpdateReceiptItem(ReceiptsClient client, Guid expenseId, Guid itemId, ReceiptItemEdit edit, CancellationToken ct = default)
    {
        if ((edit.Description is not null && edit.ClearDescription) || (edit.TaxRate is not null && edit.ClearTaxRate) || (edit.IsTaxable is not null && edit.ClearIsTaxable) || (edit.SplitRuleVersionId is not null && edit.ClearSplitRule))
        {
            throw new ArgumentException("Contradictory receipt item edit options.", nameof(edit));
        }

        var patch = new JsonPatchDocument<ReceiptItemPatch>();
        if (edit.Name is not null)
        {
            patch.Replace(x => x.Name, edit.Name);
        }

        if (edit.Description is not null || edit.ClearDescription)
        {
            patch.Replace(x => x.Description, edit.ClearDescription ? null : edit.Description);
        }

        if (edit.UnitPrice is not null)
        {
            patch.Replace(x => x.UnitPrice, edit.UnitPrice.Value);
        }

        if (edit.Quantity is not null)
        {
            patch.Replace(x => x.Quantity, edit.Quantity.Value);
        }

        if (edit.TotalPrice is not null)
        {
            patch.Replace(x => x.TotalPrice, edit.TotalPrice.Value);
        }

        if (edit.TaxAmount is not null)
        {
            patch.Replace(x => x.TaxAmount, edit.TaxAmount.Value);
        }

        if (edit.TaxRate is not null || edit.ClearTaxRate)
        {
            patch.Replace(x => x.TaxRate, edit.ClearTaxRate ? null : edit.TaxRate);
        }

        if (edit.IsTaxable is not null || edit.ClearIsTaxable)
        {
            patch.Replace(x => x.IsTaxable, edit.ClearIsTaxable ? null : edit.IsTaxable);
        }

        if (edit.SplitRuleVersionId is not null || edit.ClearSplitRule)
        {
            patch.Replace(x => x.SplitRuleVersionId, edit.ClearSplitRule ? null : edit.SplitRuleVersionId);
        }

        if (patch.Operations.Count == 0)
        {
            throw new ArgumentException("Specify at least one change.", nameof(edit));
        }

        return client.PatchReceiptItemAsync(expenseId, itemId, patch, ct);
    }

    [McpServerTool(Name = "suggest_receipt_filing", ReadOnly = true, Destructive = false, Idempotent = true), Description("Suggest a destination for receipt details without filing them.")]
    public static Task<FilingDestinationSuggestion> SuggestReceiptFiling(ReceiptsClient client, SuggestReceiptFilingRequest request, CancellationToken ct = default) => client.SuggestReceiptFilingAsync(request, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "divide_by_receipt", ReadOnly = false, Destructive = true, Idempotent = false), Description("Replace an expense's shares with its itemized receipt division. Preview and obtain user confirmation first; group balances will change.")]
    public static Task<ReceiptDivisionResponse> DivideByReceipt(ReceiptsClient client, Guid expenseId, CancellationToken ct = default) =>
        client.DivideByReceiptAsync(expenseId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_receipt", ReadOnly = false, Destructive = true, Idempotent = false), Description("Delete the itemized receipt after user confirmation. The expense shares remain.")]
    public static async Task DeleteReceipt(ReceiptsClient client, Guid expenseId, CancellationToken ct = default) =>
        await client.DeleteReceiptAsync(expenseId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_receipt_attachment", ReadOnly = false, Destructive = true, Idempotent = false), Description("Permanently delete an expense's receipt file after user confirmation.")]
    public static Task DeleteReceiptAttachment(ReceiptsClient client, Guid expenseId, Guid attachmentId, CancellationToken ct = default) => client.DeleteReceiptAttachmentAsync(expenseId, attachmentId, ct);

    [McpServerTool(Name = "list_added_receipts", ReadOnly = true, Destructive = false, Idempotent = true), Description("List receipt inbox items already added to expenses.")]
    public static Task<ICollection<ReceiptInboxResponse>> ListAddedReceipts(ReceiptsClient client, CancellationToken ct = default) => client.ListAddedReceiptInboxAsync(ct);

    [McpServerTool(Name = "list_ignored_receipts", ReadOnly = true, Destructive = false, Idempotent = true), Description("List ignored receipt inbox items.")]
    public static Task<ICollection<ReceiptInboxResponse>> ListIgnoredReceipts(ReceiptsClient client, CancellationToken ct = default) => client.ListIgnoredReceiptInboxAsync(ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_receipt_inbox_ignored", ReadOnly = false, Destructive = false, Idempotent = true), Description("Ignore or restore one of your receipt inbox items.")]
    public static Task SetReceiptInboxIgnored(ReceiptsClient client, Guid receiptId, bool ignored, CancellationToken ct = default) => ignored ? client.IgnoreInboxReceiptAsync(receiptId, ct) : client.RestoreInboxReceiptAsync(receiptId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "save_receipt_inbox", ReadOnly = false, Destructive = false, Idempotent = true), Description("Update a receipt inbox item's draft details.")]
    public static Task<ReceiptInboxResponse> SaveReceiptInbox(ReceiptsClient client, Guid receiptId, SaveReceiptInboxRequest receipt, CancellationToken ct = default) => client.SaveReceiptInboxAsync(receiptId, receipt, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "file_receipt_inbox", ReadOnly = false, Destructive = false, Idempotent = false), Description("File a receipt inbox item as an expense using the requested details after reviewing possible matches to avoid duplicates.")]
    public static Task<ReceiptInboxResponse> FileReceiptInbox(ReceiptsClient client, Guid receiptId, FileReceiptInboxRequest request, CancellationToken ct = default) => client.FileReceiptInboxAsync(receiptId, request, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_receipt_inbox", ReadOnly = false, Destructive = true, Idempotent = false), Description("Permanently delete a receipt inbox item after user confirmation.")]
    public static Task DeleteReceiptInbox(ReceiptsClient client, Guid receiptId, CancellationToken ct = default) => client.DeleteReceiptInboxAsync(receiptId, ct);

    [McpServerTool(Name = "suggest_receipt_inbox_filings", ReadOnly = true, Destructive = false, Idempotent = true), Description("Suggest filing destinations for receipt inbox items without saving changes.")]
    public static Task<ICollection<ReceiptInboxSuggestion>> SuggestReceiptInboxFilings(ReceiptsClient client, ReceiptInboxSuggestionsRequest request, CancellationToken ct = default) => client.SuggestReceiptInboxFilingsAsync(request, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "save_extracted_inbox_receipt", ReadOnly = false, Destructive = true, Idempotent = false), Description("Replace a receipt inbox item's extracted itemized receipt after reviewing it with the user.")]
    public static Task<ReceiptInboxResponse> SaveExtractedInboxReceipt(ReceiptsClient client, Guid receiptId, SaveReceiptRequest receipt, CancellationToken ct = default) => client.SaveExtractedInboxReceiptAsync(receiptId, receipt, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "dismiss_receipt_match", ReadOnly = false, Destructive = false, Idempotent = true), Description("Dismiss a suggested match between a receipt inbox item and an expense.")]
    public static Task DismissReceiptMatch(ReceiptsClient client, Guid receiptId, Guid expenseId, CancellationToken ct = default) => client.DismissReceiptInboxMatchAsync(receiptId, expenseId, ct);

    private static byte[] DecodeReceipt(string contentBase64)
    {
        const int maxBytes = 10 * 1024 * 1024;
        if (contentBase64.Length > ((maxBytes + 2) / 3) * 4)
        {
            throw new ArgumentException("Receipt exceeds the 10 MiB size limit.", nameof(contentBase64));
        }
        var bytes = Convert.FromBase64String(contentBase64);
        if (bytes.Length is 0 or > maxBytes)
        {
            throw new ArgumentException("Receipt must contain between 1 byte and 10 MiB.", nameof(contentBase64));
        }
        return bytes;
    }

    // The generated download methods discard response bodies; use the same configured API
    // client and token exchange handler to return the bytes to the MCP caller.
    private static async Task<ReceiptFileContent> DownloadReceipt(IHttpClientFactory clients, string path, CancellationToken ct)
    {
        const int maxBytes = 10 * 1024 * 1024;
        using var client = clients.CreateClient("api");
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException("Receipt exceeds the 10 MiB size limit.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > maxBytes)
            {
                throw new InvalidOperationException("Receipt exceeds the 10 MiB size limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return new ReceiptFileContent(response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", Convert.ToBase64String(output.ToArray()));
    }
}

public sealed record ReceiptFileContent(string ContentType, string ContentBase64);

public sealed record ReceiptItemEdit
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool ClearDescription { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? TotalPrice { get; init; }
    public decimal? TaxAmount { get; init; }
    public decimal? TaxRate { get; init; }
    public bool ClearTaxRate { get; init; }
    public bool? IsTaxable { get; init; }
    public bool ClearIsTaxable { get; init; }
    public Guid? SplitRuleVersionId { get; init; }
    public bool ClearSplitRule { get; init; }
}

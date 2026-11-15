using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
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
}

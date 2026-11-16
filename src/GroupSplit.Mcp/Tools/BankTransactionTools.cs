using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class BankTransactionTools
{
    [McpServerTool(Name = "get_bank_row", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get one of your imported bank rows.")]
    public static Task<BankTransactionResponse> GetBankRow(InboxClient client, Guid bankRowId, CancellationToken ct = default) => client.GetBankTransactionAsync(bankRowId, ct);

    [McpServerTool(Name = "search_receipt_bank_rows", ReadOnly = true, Destructive = false, Idempotent = true), Description("Search imported bank rows when matching a receipt, 100 per page.")]
    public static Task<PagedResponseOfBankTransactionResponse> SearchReceiptBankRows(InboxClient client, string? search = null, int page = 1, CancellationToken ct = default) => client.SearchReceiptBankTransactionsAsync(search!, Math.Max(1, page), 100, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "dismiss_bank_match", ReadOnly = false, Destructive = false, Idempotent = true), Description("Dismiss a suggested match between a bank row and an expense.")]
    public static Task DismissBankMatch(InboxClient client, Guid bankRowId, DismissBankMatchRequest match, CancellationToken ct = default) => client.DismissBankTransactionMatchAsync(bankRowId, match, ct);
    [McpServerTool(Name = "suggest_bank_filings", ReadOnly = true, Destructive = false, Idempotent = true), Description("Suggest filing destinations for imported bank rows without creating expenses.")]
    public static Task<ICollection<FilingSuggestionResponse>> SuggestBankFilings(InboxClient client, SuggestFilingsRequest request, CancellationToken ct = default) =>
        client.SuggestBankTransactionFilingsAsync(request, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "file_bank_row", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create an expense from an imported bank row. A possible duplicate is refused unless the user explicitly confirms it is a separate payment and fileAnyway is set.")]
    public static Task<TransactionResponse> FileBankRow(InboxClient client, Guid bankRowId, FileBankTransactionRequest request, CancellationToken ct = default) =>
        client.FileBankTransactionAsync(bankRowId, request, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "link_bank_row", ReadOnly = false, Destructive = false, Idempotent = false), Description("Link an imported bank row to an existing expense instead of creating a duplicate.")]
    public static Task<TransactionResponse> LinkBankRow(InboxClient client, Guid bankRowId, Guid expenseId, CancellationToken ct = default) =>
        client.LinkBankTransactionAsync(bankRowId, new LinkBankTransactionRequest { TransactionId = expenseId }, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_bank_row_ignored", ReadOnly = false, Destructive = false, Idempotent = true), Description("Ignore an imported bank row or restore a previously ignored row.")]
    public static async Task<bool> SetBankRowIgnored(InboxClient client, Guid bankRowId, bool ignored, CancellationToken ct = default)
    {
        if (ignored)
        {
            await client.IgnoreBankTransactionAsync(bankRowId, ct);
        }
        else
        {
            await client.RestoreBankTransactionAsync(bankRowId, ct);
        }
        return true;
    }

    [McpServerTool(Name = "expense_bank_matches", ReadOnly = true, Destructive = false, Idempotent = true), Description("Find bank rows linked to an expense you can access.")]
    public static Task<ICollection<BankTransactionResponse>> ExpenseBankMatches(TransactionsClient client, Guid expenseId, CancellationToken ct = default) =>
        client.GetTransactionBankMatchesAsync(expenseId, ct);

    [McpServerTool(Name = "bank_inbox", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your imported bank rows, 100 per page, optionally filtering by status and date.")]
    public static Task<PagedResponseOfBankTransactionResponse> BankInbox(InboxClient client, InboxStatus? status = null, DateOnly? from = null, DateOnly? to = null, string? search = null, int page = 1, CancellationToken ct = default) =>
        client.GetInboxAsync(status: status, from: from, to: to, search: search!, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "bank_inbox_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize your imported bank inbox. Optionally include duplicate candidates.")]
    public static Task<InboxSummaryResponse> BankInboxSummary(InboxClient client, bool withDuplicates = false, CancellationToken ct = default) =>
        client.GetInboxSummaryAsync(withDuplicates, ct);

    [McpServerTool(Name = "bank_row_matches", ReadOnly = true, Destructive = false, Idempotent = true), Description("Find possible matching expenses for one of your imported bank rows.")]
    public static Task<ICollection<ExpenseMatchResponse>> BankRowMatches(InboxClient client, Guid bankRowId, CancellationToken ct = default) =>
        client.GetBankTransactionMatchesAsync(bankRowId, ct);
}

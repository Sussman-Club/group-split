using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class TransactionTools
{
    [McpServerTool(Name = "list_group_expenses", ReadOnly = true, Destructive = false, Idempotent = true), Description("List group expenses, 100 per page.")]
    public static Task<PagedResponseOfTransactionResponse> ListGroupExpenses(GroupsClient client, Guid groupId, int page = 1, CancellationToken ct = default) => client.GetGroupTransactionsAsync(groupId, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "list_expenses", ReadOnly = true, Destructive = false, Idempotent = true), Description("List expenses you paid, 100 per page.")]
    public static Task<PagedResponseOfTransactionResponse> ListExpenses(TransactionsClient client, int page = 1, CancellationToken ct = default) => client.GetTransactionsAsync(page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "get_expense", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get an accessible expense and its splits.")]
    public static Task<TransactionDetailsResponse> GetExpense(TransactionsClient client, Guid expenseId, CancellationToken ct = default) => client.GetTransactionAsync(expenseId, cancellationToken: ct);

    [McpServerTool(Name = "preview_expense", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview how an expense would be split without saving it. Supply its name, amount, dateTime, and any group or split details in expense.")]
    public static Task<SplitPreviewResponse> PreviewExpense(TransactionsClient client, CreateTransactionRequest expense, CancellationToken ct = default) =>
        client.PreviewTransactionSplitsAsync(expense, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_expense", ReadOnly = false, Destructive = false, Idempotent = false), Description("Record an expense. Supply its name, amount, dateTime, and any group or split details in expense; preview first when the split needs review.")]
    public static Task<TransactionResponse> CreateExpense(TransactionsClient client, CreateTransactionRequest expense, CancellationToken ct = default) =>
        client.CreateTransactionAsync(expense, ct);

    [McpServerTool(Name = "expense_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize expenses you paid, optionally within a group and date range.")]
    public static Task<TransactionSummaryResponse> ExpenseSummary(TransactionsClient client, Guid? groupId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
        client.GetTransactionsSummaryAsync(from: from, to: to, groupId: groupId, cancellationToken: ct);

    [McpServerTool(Name = "monthly_exposure", ReadOnly = true, Destructive = false, Idempotent = true), Description("Show what you paid and your share by month.")]
    public static Task<ICollection<MonthlyExposureResponse>> MonthlyExposure(TransactionsClient client, Guid? groupId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
        client.GetMonthlyExposureAsync(from: from, to: to, groupId: groupId, cancellationToken: ct);

    [McpServerTool(Name = "expense_shares", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your shares of expenses, 100 per page, with optional group, payer, date, and owed-only filters.")]
    public static Task<PagedResponseOfExpenseShareResponse> ExpenseShares(TransactionsClient client, Guid? groupId = null, Guid? paidByUserId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, bool? owedOnly = null, int page = 1, CancellationToken ct = default) =>
        client.GetTransactionSharesAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, owedOnly: owedOnly, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "expense_shares_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize your expense shares with optional group, payer, date, and owed-only filters.")]
    public static Task<ExpenseShareSummaryResponse> ExpenseSharesSummary(TransactionsClient client, Guid? groupId = null, Guid? paidByUserId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, bool? owedOnly = null, CancellationToken ct = default) =>
        client.GetTransactionSharesSummaryAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, owedOnly: owedOnly, cancellationToken: ct);
}

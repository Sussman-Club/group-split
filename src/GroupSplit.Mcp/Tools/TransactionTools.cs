using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class TransactionTools
{
    [McpServerTool(Name = "list_group_expenses", ReadOnly = true, Destructive = false, Idempotent = true), Description("List group expenses, 100 per page.")]
    public static Task<PagedResponseOfTransactionResponse> ListGroupExpenses(GroupsClient client, Guid groupId, int page = 1, DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidByUserId = null, string? category = null, string? search = null, string? sortBy = null, bool? sortDescending = null, CancellationToken ct = default) => client.GetGroupTransactionsAsync(groupId, from: from, to: to, paidByUserId: paidByUserId, category: category!, search: search!, sortBy: sortBy!, sortDescending: sortDescending, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "list_expenses", ReadOnly = true, Destructive = false, Idempotent = true), Description("List expenses you paid, 100 per page.")]
    public static Task<PagedResponseOfTransactionResponse> ListExpenses(TransactionsClient client, int page = 1, Guid? groupId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidByUserId = null, string? category = null, bool? personal = null, string? search = null, string? sortBy = null, bool? sortDescending = null, CancellationToken ct = default) => client.GetTransactionsAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, category: category!, personal: personal, search: search!, sortBy: sortBy!, sortDescending: sortDescending, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "get_expense", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get an accessible expense and its splits.")]
    public static Task<TransactionDetailsResponse> GetExpense(TransactionsClient client, Guid expenseId, CancellationToken ct = default) => client.GetTransactionAsync(expenseId, cancellationToken: ct);

    [McpServerTool(Name = "preview_expense", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview how an expense would be split without saving it. Supply its name, amount, dateTime, and any group or split details in expense.")]
    public static Task<SplitPreviewResponse> PreviewExpense(TransactionsClient client, CreateTransactionRequest expense, CancellationToken ct = default) =>
        client.PreviewTransactionSplitsAsync(expense, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_expense", ReadOnly = false, Destructive = false, Idempotent = false), Description("Record an expense. Supply its name, amount, dateTime, and any group or split details in expense; preview first when the split needs review.")]
    public static Task<TransactionResponse> CreateExpense(TransactionsClient client, CreateTransactionRequest expense, CancellationToken ct = default) =>
        client.CreateTransactionAsync(expense, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_transaction", ReadOnly = false, Destructive = true, Idempotent = false), Description("Permanently delete an expense or settlement after the user confirms. Deleting a settlement changes balances.")]
    public static async Task DeleteTransaction(TransactionsClient client, Guid transactionId, CancellationToken ct = default) =>
        await client.DeleteTransactionAsync(transactionId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "update_transaction", ReadOnly = false, Destructive = true, Idempotent = false), Description("Change only the supplied transaction fields. Changes to amount, payer, group, category or splits may change balances; review and confirm first. Use clear flags to remove optional fields. Redivide explicitly recalculates splits.")]
    public static Task<TransactionDetailsResponse> UpdateTransaction(TransactionsClient client, Guid transactionId, TransactionEdit edit, CancellationToken ct = default)
    {
        if ((edit.GroupId is not null && edit.MakePersonal) || (edit.CategoryId is not null && edit.ClearCategory) ||
            (edit.MerchantId is not null && edit.ClearMerchant) || (edit.CoversThrough is not null && edit.ClearCoversThrough) ||
            (edit.Splits is not null && (edit.Redivide || edit.SplitRuleId is not null)) || (edit.Redivide && edit.SplitRuleId is not null))
        {
            throw new ArgumentException("Contradictory transaction edit options.", nameof(edit));
        }
        var patch = new JsonPatchDocument<UpdateTransactionRequest>();
        if (edit.Name is not null)
        {
            patch.Replace(x => x.Name, edit.Name);
        }

        if (edit.Amount is not null)
        {
            patch.Replace(x => x.Amount, edit.Amount.Value);
        }

        if (edit.Currency is not null)
        {
            patch.Replace(x => x.Currency, edit.Currency);
        }

        if (edit.DateTime is not null)
        {
            patch.Replace(x => x.DateTime, edit.DateTime.Value);
        }

        if (edit.Description is not null)
        {
            patch.Replace(x => x.Description, edit.Description);
        }

        if (edit.PaidByUserId is not null)
        {
            patch.Replace(x => x.PaidByUserId, edit.PaidByUserId.Value);
        }

        if (edit.GroupId is not null || edit.MakePersonal)
        {
            patch.Replace(x => x.GroupId, edit.MakePersonal ? null : edit.GroupId);
        }

        if (edit.CategoryId is not null || edit.ClearCategory)
        {
            patch.Replace(x => x.CategoryId, edit.ClearCategory ? null : edit.CategoryId);
        }

        if (edit.MerchantId is not null || edit.ClearMerchant)
        {
            patch.Replace(x => x.MerchantId, edit.ClearMerchant ? null : edit.MerchantId);
        }

        if (edit.CoversThrough is not null || edit.ClearCoversThrough)
        {
            patch.Replace(x => x.CoversThrough, edit.ClearCoversThrough ? null : edit.CoversThrough);
        }

        if (edit.SplitRuleId is not null)
        {
            patch.Replace(x => x.SplitRuleId, edit.SplitRuleId);
        }

        if (edit.Splits is not null || edit.Redivide)
        {
            patch.Replace(x => x.Splits, edit.Redivide ? null : edit.Splits);
        }

        if (patch.Operations.Count == 0)
        {
            throw new ArgumentException("Specify at least one change.", nameof(edit));
        }

        return client.UpdateTransactionAsync(transactionId, patch, ct);
    }

    [McpServerTool(Name = "preview_transaction_update", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview the split for a complete proposed transaction update without saving it. Set redivide to preview dividing by the category again.")]
    public static Task<SplitPreviewResponse> PreviewTransactionUpdate(TransactionsClient client, Guid transactionId, UpdateTransactionRequest transaction, bool redivide = false, CancellationToken ct = default) =>
        client.PreviewUpdatedTransactionSplitsAsync(transactionId, transaction, redivide ? true : null, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_transaction_division_source", ReadOnly = false, Destructive = false, Idempotent = true), Description("Record which split-rule version produced an expense's existing shares; null marks a manual division. This records provenance without changing shares.")]
    public static Task<TransactionDetailsResponse> SetDivisionSource(TransactionsClient client, Guid transactionId, Guid? splitRuleVersionId = null, CancellationToken ct = default) =>
        client.SetTransactionDivisionSourceAsync(transactionId, new SetDivisionSourceRequest(splitRuleVersionId), ct);

    [McpServerTool(Name = "expense_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize expenses you paid, optionally within a group and date range.")]
    public static Task<TransactionSummaryResponse> ExpenseSummary(TransactionsClient client, Guid? groupId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidByUserId = null, string? category = null, bool? personal = null, string? search = null, CancellationToken ct = default) =>
        client.GetTransactionsSummaryAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, category: category!, personal: personal, search: search!, cancellationToken: ct);

    [McpServerTool(Name = "monthly_exposure", ReadOnly = true, Destructive = false, Idempotent = true), Description("Show what you paid and your share by month.")]
    public static Task<ICollection<MonthlyExposureResponse>> MonthlyExposure(TransactionsClient client, Guid? groupId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidByUserId = null, string? category = null, bool? personal = null, string? search = null, CancellationToken ct = default) =>
        client.GetMonthlyExposureAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, category: category!, personal: personal, search: search!, cancellationToken: ct);

    [McpServerTool(Name = "expense_shares", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your shares of expenses, 100 per page, with optional group, payer, date, and owed-only filters.")]
    public static Task<PagedResponseOfExpenseShareResponse> ExpenseShares(TransactionsClient client, Guid? groupId = null, Guid? paidByUserId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, bool? owedOnly = null, int page = 1, string? category = null, bool? personal = null, string? search = null, string? sortBy = null, bool? sortDescending = null, CancellationToken ct = default) =>
        client.GetTransactionSharesAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, category: category!, personal: personal, search: search!, sortBy: sortBy!, sortDescending: sortDescending, owedOnly: owedOnly, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "expense_shares_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize your expense shares with optional group, payer, date, and owed-only filters.")]
    public static Task<ExpenseShareSummaryResponse> ExpenseSharesSummary(TransactionsClient client, Guid? groupId = null, Guid? paidByUserId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, bool? owedOnly = null, string? category = null, bool? personal = null, string? search = null, CancellationToken ct = default) =>
        client.GetTransactionSharesSummaryAsync(from: from, to: to, groupId: groupId, paidByUserId: paidByUserId, category: category!, personal: personal, search: search!, owedOnly: owedOnly, cancellationToken: ct);
}

public sealed record TransactionEdit
{
    public string? Name { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public DateTimeOffset? DateTime { get; init; }
    public string? Description { get; init; }
    public Guid? PaidByUserId { get; init; }
    public Guid? GroupId { get; init; }
    public bool MakePersonal { get; init; }
    public Guid? CategoryId { get; init; }
    public bool ClearCategory { get; init; }
    public Guid? MerchantId { get; init; }
    public bool ClearMerchant { get; init; }
    public Guid? SplitRuleId { get; init; }
    public IReadOnlyList<SplitInput>? Splits { get; init; }
    public bool Redivide { get; init; }
    public DateOnly? CoversThrough { get; init; }
    public bool ClearCoversThrough { get; init; }
}

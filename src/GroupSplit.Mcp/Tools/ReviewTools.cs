using System.ComponentModel;
using GroupSplit.Shared;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ReviewTools
{
    [McpServerTool(Name = "household_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize one accessible group's spending and the caller's paid and share amounts over explicit inclusive instants. Monthly exposure is grouped by UTC month and may omit non-headline currencies; inspect the spending breakdown for all currencies.")]
    public static async Task<HouseholdSummary> HouseholdSummary(GroupsClient groups, TransactionsClient transactions, Guid groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The end must be at or after the start.");
        }
        var spending = await groups.GetGroupSpendingByCategoryAsync(groupId, from: from, to: to, paidBy: null, categoryId: null, uncategorized: null, search: null!, category: null!, cancellationToken: ct);
        var exposure = await transactions.GetMonthlyExposureAsync(from: from, to: to, groupId: groupId, paidByUserId: null, category: null!, personal: null, search: null!, cancellationToken: ct);
        return new(from, to, spending, exposure);
    }
    [McpServerTool(Name = "review_receipt_inbox_item", ReadOnly = true, Destructive = false, Idempotent = true), Description("Review one receipt inbox item and possible expense matches without changing anything. Matches are candidates, not proof of duplication.")]
    public static async Task<ReceiptInboxReview> ReviewReceiptInboxItem(ReceiptsClient client, Guid receiptId, CancellationToken ct = default)
    {
        var receipt = await client.GetReceiptInboxAsync(receiptId, ct);
        var matches = await client.MatchReceiptInboxAsync(receiptId, ct);
        return new(receipt, matches);
    }
}

public sealed record ReceiptInboxReview(ReceiptInboxResponse Receipt, ICollection<ReceiptExpenseCandidate> Matches);

public sealed record HouseholdSummary(DateTimeOffset From, DateTimeOffset To, CategorySpendingSummaryResponse Spending, ICollection<MonthlyExposureResponse> Exposure);

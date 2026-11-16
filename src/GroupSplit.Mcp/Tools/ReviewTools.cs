using System.ComponentModel;
using GroupSplit.Shared;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ReviewTools
{
    [McpServerTool(Name = "household_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize an accessible group's local calendar month. Supply a time zone such as America/New_York. Spending and the caller's paid and share amounts are separate for each currency; settlements are excluded.")]
    public static Task<HouseholdMonthResponse> HouseholdSummary(GroupsClient groups, Guid groupId, int year, int month, string timeZone, CancellationToken ct = default) =>
        groups.GetHouseholdMonthAsync(groupId, year, month, timeZone, ct);
    [McpServerTool(Name = "review_receipt_inbox_item", ReadOnly = true, Destructive = false, Idempotent = true), Description("Review one receipt inbox item and possible expense matches without changing anything. Matches are candidates, not proof of duplication.")]
    public static async Task<ReceiptInboxReview> ReviewReceiptInboxItem(ReceiptsClient client, Guid receiptId, CancellationToken ct = default)
    {
        var receipt = await client.GetReceiptInboxAsync(receiptId, ct);
        var matches = await client.MatchReceiptInboxAsync(receiptId, ct);
        return new(receipt, matches);
    }
}

public sealed record ReceiptInboxReview(ReceiptInboxResponse Receipt, ICollection<ReceiptExpenseCandidate> Matches);

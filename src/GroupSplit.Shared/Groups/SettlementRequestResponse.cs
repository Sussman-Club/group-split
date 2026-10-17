namespace GroupSplit.Shared;

public enum SettlementRequestStatus { Open, Stale, Paid, Cancelled }

/// <summary>A shareable invitation to record a repayment; never a ledger entry itself.</summary>
public record SettlementRequestResponse(
    Guid Id, string Token, SettlementRequestStatus Status,
    Guid RequesterUserId, string RequesterName, Guid RecipientUserId, string RecipientName,
    decimal Amount, string Currency, DateTimeOffset CreatedAt, IReadOnlyList<GroupDebt> Groups);

public record CreateSettlementRequest
{
    public Guid RecipientUserId { get; set; }
    public decimal Amount { get; set; }
}

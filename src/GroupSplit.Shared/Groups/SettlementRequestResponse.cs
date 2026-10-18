namespace GroupSplit.Shared;

public enum SettlementRequestStatus { Open, Stale, Paid, Cancelled }

/// <summary>A shareable invitation to record a repayment; never a ledger entry itself.</summary>
public record SettlementRequestResponse(
    Guid Id, string Token, SettlementRequestStatus Status,
    Guid RequesterUserId, string RequesterName, Guid RecipientUserId, string RecipientName,
    decimal Amount, string Currency, DateTimeOffset CreatedAt, IReadOnlyList<GroupDebt> Groups,
    DateOnly? Through = null);

public record CreateSettlementRequest
{
    public Guid RecipientUserId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly? Through { get; set; }

    [System.ComponentModel.DataAnnotations.Range(-Cutoff.MaxOffsetMinutes, Cutoff.MaxOffsetMinutes)]
    public int? UtcOffset { get; set; }
}

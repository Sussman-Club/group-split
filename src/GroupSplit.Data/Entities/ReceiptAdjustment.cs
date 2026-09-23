using GroupSplit.Shared;

namespace GroupSplit.Data.Entities;

/// <summary>A coupon or discount printed outside the purchased item rows.</summary>
public class ReceiptAdjustment : Entity
{
    public virtual Receipt Receipt { get; set; } = null!;
    public Guid ReceiptId { get; set; }
    public int Position { get; set; }
    public ReceiptAdjustmentKind Kind { get; set; }
    /// <summary>Always positive. A percentage is expressed as 10 for ten percent.</summary>
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? CouponCode { get; set; }
}

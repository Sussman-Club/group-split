namespace GroupSplit.Data.Entities;

/// <summary>
/// A request to record a repayment that happened outside GroupSplit. It is deliberately not
/// a transaction: creating, sharing, cancelling, or opening one must never move a balance.
/// </summary>
public class SettlementRequest : Entity
{
    public virtual User Requester { get; set; } = null!;
    public Guid RequesterUserId { get; set; }
    public virtual User Recipient { get; set; } = null!;
    public Guid RecipientUserId { get; set; }
    /// <summary>The opaque, high-entropy part of the share URL.</summary>
    public required string Token { get; set; }
    public required string Currency { get; set; }
    public required decimal Amount { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? StaleAt { get; set; }
    /// <summary>The balances the requester saw, and the requested parts, by group.</summary>
    public virtual ICollection<SettlementRequestGroup> Groups { get; } = [];
}

/// <summary>One group's contribution to a settlement request.</summary>
public class SettlementRequestGroup
{
    public Guid SettlementRequestId { get; set; }
    public virtual SettlementRequest SettlementRequest { get; set; } = null!;
    public Guid GroupId { get; set; }
    public virtual Group Group { get; set; } = null!;
    /// <summary>The full debt in this group when the request was created.</summary>
    public required decimal DebtAtCreation { get; set; }
    /// <summary>The part of the request that will be recorded in this group.</summary>
    public required decimal RequestedAmount { get; set; }
}

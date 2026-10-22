namespace GroupSplit.Data.Entities;

/// <summary>Credential-free operational work that must survive local data deletion.</summary>
public class PendingDataCleanup : Entity
{
    public required string Kind { get; set; }
    public required string Resource { get; set; }
    public string? Provider { get; set; }
    public Guid? ConnectionId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

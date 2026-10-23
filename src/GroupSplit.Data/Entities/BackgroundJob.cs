namespace GroupSplit.Data.Entities;

public enum BackgroundJobStatus { Pending, Running, Completed, Failed, AwaitingOperator }

/// <summary>A durable invocation. Payloads contain identifiers, never credentials.</summary>
public sealed class BackgroundJob : Entity
{
    public required string Type { get; set; }
    public int Version { get; set; } = 1;
    /// <summary>Serialized typed input; PostgreSQL stores and validates it as jsonb.</summary>
    public required string Payload { get; set; }
    public BackgroundJobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>A safe code or operator instruction, not an exception message.</summary>
    public string? Reason { get; set; }
}

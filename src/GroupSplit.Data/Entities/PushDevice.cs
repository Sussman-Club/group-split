namespace GroupSplit.Data.Entities;

public sealed class PushDevice : Entity
{
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public bool ExpensesEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Short-lived delivery outbox, committed in the same transaction as the expense.
public sealed class PushDelivery : Entity
{
    public Guid DeviceId { get; set; }
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
}

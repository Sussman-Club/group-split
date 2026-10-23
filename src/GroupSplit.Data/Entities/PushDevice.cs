namespace GroupSplit.Data.Entities;

public sealed class PushDevice : Entity
{
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public bool ExpensesEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

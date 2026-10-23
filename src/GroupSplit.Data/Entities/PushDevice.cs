namespace GroupSplit.Data.Entities;

public sealed class PushDevice : Entity
{
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public bool ExpensesEnabled { get; set; }
    public bool SettlementsEnabled { get; set; }
    public bool InvitationsEnabled { get; set; }
    public bool BankSyncIssuesEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

namespace GroupSplit.Data.Entities;

public class Transaction : Entity
{
    public virtual User User { get; set; } = null!;
    public virtual RuleVersion RuleVersion { get; set; } = null!;
    public required decimal Amount { get; set; }
    public required DateTimeOffset DateTime { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}
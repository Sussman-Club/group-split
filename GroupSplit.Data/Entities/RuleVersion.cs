namespace GroupSplit.Data.Entities;

public abstract class RuleVersion : Entity
{
    public virtual Rule Rule { get; set; } = null!;
    public required DateTimeOffset StartDateTime { get; init; }
    public DateTimeOffset? EndDateTime { get; init; }
    public virtual ICollection<Transaction> Transactions { get; } = []; 
}
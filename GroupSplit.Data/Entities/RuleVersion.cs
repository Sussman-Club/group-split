namespace GroupSplit.Data.Entities;

public abstract class RuleVersion : Entity
{
    public virtual Rule Rule { get; set; } = null!;
    
    public required DateOnly StartDate { get; init; }
}
namespace GroupSplit.Data.Entities;

public class Rule : Entity
{
    public virtual Group Group { get; set; } = null!;

    public required string Category { get; init; }
    
    public virtual ICollection<RuleVersion> Versions { get; } = [];
}
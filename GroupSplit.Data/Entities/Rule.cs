namespace GroupSplit.Data.Entities;

public class Rule : Entity
{
    public const string Settlement = "Settlement";
    
    public virtual Group Group { get; set; } = null!;

    internal Guid GroupId { get; set; }
    
    public required string Category { get; set; }
    
    public RuleFlags Flags { get; init; }
    
    public virtual ICollection<RuleVersion> Versions { get; } = [];
}
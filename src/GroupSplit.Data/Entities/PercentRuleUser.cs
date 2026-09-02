namespace GroupSplit.Data.Entities;

public class PercentRuleUser : Entity
{
    public virtual PercentRuleVersion RuleVersion { get; set; } = null!;
    
    internal Guid RuleVersionId { get; set; } // Used for EF Core indexes

    public virtual User User { get; set; } = null!;
    
    internal Guid UserId { get; set; } // Used for EF Core indexes
    
    public required double Percentage { get; init; }
}
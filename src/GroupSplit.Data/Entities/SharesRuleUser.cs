namespace GroupSplit.Data.Entities;

public class SharesRuleUser : Entity
{
    public virtual SharesRuleVersion RuleVersion { get; set; } = null!;
    
    internal Guid RuleVersionId { get; set; } // Used for EF Core indexes

    public virtual User User { get; set; } = null!;
    
    internal Guid UserId { get; set; } // Used for EF Core indexes
    
    public required int Shares { get; init; }
}
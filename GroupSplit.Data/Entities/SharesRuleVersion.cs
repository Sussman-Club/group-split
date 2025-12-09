namespace GroupSplit.Data.Entities;

public class SharesRuleVersion : RuleVersion
{
    public virtual ICollection<SharesRuleUser> RuleUsers { get; } = [];
}
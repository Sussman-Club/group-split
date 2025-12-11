namespace GroupSplit.Data.Entities;

public class SharesRuleVersion : PercentRuleVersion
{
    public virtual ICollection<SharesRuleUser> SharedRuleUsers { get; } = [];
}
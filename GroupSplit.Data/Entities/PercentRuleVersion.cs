namespace GroupSplit.Data.Entities;

public class PercentRuleVersion : RuleVersion
{
    public virtual ICollection<PercentRuleUser> RuleUsers { get; } = [];
}
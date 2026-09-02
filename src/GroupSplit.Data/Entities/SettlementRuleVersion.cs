namespace GroupSplit.Data.Entities;

public class SettlementRuleVersion : RuleVersion
{
    public virtual User OtherUser { get; init; } = null!;
    internal Guid OtherUserId { get; set; }
}
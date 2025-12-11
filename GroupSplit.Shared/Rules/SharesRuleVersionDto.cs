namespace GroupSplit.Shared;

public record SharesRuleVersionDto : RuleVersionDto
{
    public Dictionary<Guid, int> Shares { get; init; } = new();
}
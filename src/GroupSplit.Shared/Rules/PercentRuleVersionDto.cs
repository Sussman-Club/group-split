namespace GroupSplit.Shared;

public record PercentRuleVersionDto : RuleVersionDto
{
    public Dictionary<Guid, decimal> Percentages { get; init; } = new();
}
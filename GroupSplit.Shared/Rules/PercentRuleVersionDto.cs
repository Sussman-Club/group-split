using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record PercentRuleVersionDto : RuleVersionDto
{
    [NonNegativeValues(ErrorMessage = "Percentages must be non-negative.")]
    public Dictionary<Guid, decimal> Percentages { get; init; } = new();
}
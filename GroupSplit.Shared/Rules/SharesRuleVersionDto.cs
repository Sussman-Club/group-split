using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record SharesRuleVersionDto : RuleVersionDto
{
    [NonNegativeValues(ErrorMessage = "Shares must be non-negative.")]
    public Dictionary<Guid, int> Shares { get; init; } = new();
}
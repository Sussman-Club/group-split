using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// How a split rule divides, on the wire. One shape per kind, told apart by
/// <c>$type</c>, as the rule-version DTOs already are.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(EvenSplitRuleDto), typeDiscriminator: "even")]
[JsonDerivedType(typeof(PayerSplitRuleDto), typeDiscriminator: "payer")]
[JsonDerivedType(typeof(PercentSplitRuleDto), typeDiscriminator: "percent")]
[JsonDerivedType(typeof(SharesSplitRuleDto), typeDiscriminator: "shares")]
public abstract record SplitRuleDto;

/// <summary>
/// Equally between people.
/// </summary>
/// <param name="Among">
/// Who to divide between, or empty for the whole group. Empty is the useful default: it
/// keeps dividing evenly when somebody joins, rather than freezing today's members in.
/// </param>
public record EvenSplitRuleDto(IReadOnlyList<Guid> Among) : SplitRuleDto
{
    public EvenSplitRuleDto() : this([])
    {
    }
}

/// <summary>
/// In proportion to percentages, stated the way a person states them -- 33.33 for a third.
/// </summary>
/// <remarks>
/// Percent rather than the hundredths the rule is stored in, because this is what a member
/// types and what the form shows. The conversion happens once, at the edge.
/// </remarks>
public record PercentSplitRuleDto : SplitRuleDto
{
    public Dictionary<Guid, decimal> Percentages { get; init; } = new();
}

/// <summary>
/// In proportion to whole shares.
/// </summary>
public record SharesSplitRuleDto : SplitRuleDto
{
    public Dictionary<Guid, int> Shares { get; init; } = new();
}

/// <summary>
/// Not shared: whoever paid owes all of it. Carries nothing, because who owes depends on
/// who paid and that is not known until the expense is written.
/// </summary>
public record PayerSplitRuleDto : SplitRuleDto;

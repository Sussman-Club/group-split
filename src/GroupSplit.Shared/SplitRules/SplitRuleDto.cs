using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// How a split rule divides, on the wire. One shape per kind, told apart by
/// <c>$type</c>, as the rule-version DTOs already are.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(EvenSplitRuleDto), typeDiscriminator: "even")]
[JsonDerivedType(typeof(ItemizedSplitRuleDto), typeDiscriminator: "itemized")]
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

/// <summary>
/// By the bill: each person owes the lines they claimed, plus their share of the tax and the
/// tip in proportion to what they claimed.
/// </summary>
/// <remarks>
/// Carries nothing, for a sharper version of the reason <see cref="PayerSplitRuleDto"/> does
/// not: who owes what is on the receipt attached to the expense, which is different for every
/// expense filed under this rule and is not known when the rule is written. Attaching the
/// bill and saying who had which line are done against the expense, not here.
/// <para>
/// An expense filed under this with no receipt cannot be divided, and says so by name rather
/// than falling back to an even split -- a silent fallback here would quietly charge five
/// people equally for a dinner somebody itemised precisely to avoid that.
/// </para>
/// </remarks>
public record ItemizedSplitRuleDto : SplitRuleDto;

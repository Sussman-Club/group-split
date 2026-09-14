using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// How a split rule divides, on the wire. One shape per kind, told apart by
/// <c>$type</c>, as the rule-version DTOs already are.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(EvenSplitRuleDto), typeDiscriminator: "even")]
[JsonDerivedType(typeof(ItemizedSplitRuleDto), typeDiscriminator: "itemized")]
[JsonDerivedType(typeof(PercentSplitRuleDto), typeDiscriminator: "percent")]
[JsonDerivedType(typeof(SharesSplitRuleDto), typeDiscriminator: "shares")]
[JsonDerivedType(typeof(SoleSplitRuleDto), typeDiscriminator: "sole")]
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
/// Not shared: the whole amount is one person's.
/// </summary>
/// <param name="UserId">
/// Who owes it: a member, or somebody the group has invited and is waiting on -- the same
/// set any other rule may name.
/// </param>
public record SoleSplitRuleDto(Guid UserId) : SplitRuleDto
{
    public SoleSplitRuleDto() : this(Guid.Empty)
    {
    }
}

/// <summary>
/// By the bill: each line is divided by the rule pinned to it, and everybody owes what the
/// lines they were named on came to, plus the tax charged on them and their share of the tip.
/// </summary>
/// <remarks>
/// Carries nothing, unlike every other kind: who owes what is on the receipt attached to the
/// expense, which is different for every expense filed under this rule and is not known when
/// the rule is written. Attaching the bill and choosing each line's rule are done against the
/// expense, not here.
/// <para>
/// An expense filed under this with no receipt cannot be divided, and says so by name rather
/// than falling back to an even split -- a silent fallback here would quietly charge five
/// people equally for a dinner somebody itemised precisely to avoid that.
/// </para>
/// </remarks>
public record ItemizedSplitRuleDto : SplitRuleDto;

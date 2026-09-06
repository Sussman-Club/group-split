using GroupSplit.Data.Entities;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// The part every proportional rule shares: hand the weights to the division, and refuse a
/// rule that names somebody twice.
/// </summary>
/// <remarks>
/// A base class rather than a duplicated pair of methods, because the three proportional
/// kinds differ only in how their weights are read, how they are shown, and what makes
/// them invalid.
/// </remarks>
public abstract class WeightedSplitRuleHandler<TRule> : ISplitRuleHandler<TRule>
    where TRule : WeightedSplitRule
{
    public IReadOnlyList<SplitAmount> Divide(
        TRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members) =>
        SplitCalculator.Divide(amount, payerId, WeightsFor(rule, members));

    public virtual string? Invalid(TRule rule) =>
        rule.Participants.Select(participant => participant.UserId).Distinct().Count() == rule.Participants.Count
            ? null
            : "A member may appear in a rule only once.";

    public abstract Shared.SplitRuleDto ToDto(TRule rule);

    protected abstract IReadOnlyList<SplitWeight> WeightsFor(TRule rule, IReadOnlyCollection<Guid> members);

    /// <summary>The participants' weights as they are stored.</summary>
    protected static IReadOnlyList<SplitWeight> StoredWeights(WeightedSplitRule rule) =>
        [..rule.Participants.Select(participant => new SplitWeight(participant.UserId, participant.Weight))];

    /// <summary>Fills a new rule's participants from user-to-weight pairs.</summary>
    protected static TNew Naming<TNew>(TNew rule, IEnumerable<KeyValuePair<Guid, int>> weights)
        where TNew : WeightedSplitRule
    {
        foreach (var (userId, weight) in weights)
            rule.Participants.Add(new SplitRuleParticipant { UserId = userId, Weight = weight });

        return rule;
    }
}

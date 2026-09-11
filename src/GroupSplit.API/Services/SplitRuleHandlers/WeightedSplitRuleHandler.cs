using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;

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
    where TRule : WeightedSplitRuleVersion
{
    public IReadOnlyList<SplitAmount> Divide(
        TRule rule, Transaction transaction, IReadOnlyCollection<Guid> members) =>
        SplitCalculator.Divide(
            transaction.Amount, transaction.Payer, Among(WeightsFor(rule, members), members));

    /// <summary>
    /// The weights, less anybody who is no longer one of <paramref name="members"/>.
    /// </summary>
    /// <remarks>
    /// Here rather than in each <see cref="WeightsFor"/>, so that every proportional kind is
    /// covered by construction and a kind added later cannot forget it.
    /// <para>
    /// A version is history and goes on naming whoever it named -- that is what makes an old
    /// expense divisible again by the rule it had. But dividing by it <em>now</em> pays
    /// people who are in it, and somebody who has left the group is not one of them: their
    /// share would land on a person the group's balances no longer list, so the column would
    /// stop summing to zero with the missing side belonging to nobody on the page. Weights
    /// are proportional and the division normalises by whatever total it is given, so
    /// dropping them redistributes what was theirs among the rest -- which is what
    /// <c>docs/split-rules-and-membership.md</c> says a departure means, and what
    /// <c>ISplitRuleRevisions</c> already does to the version the rule goes on to.
    /// </para>
    /// <para>
    /// Members here is participation and not membership: somebody invited and still to
    /// answer keeps their weight, because their share is an ordinary share. The mirror of
    /// <see cref="ExpenseSplitter"/> refusing a <em>stated</em> split that names a
    /// non-participant (<c>SPLIT_USER_NOT_IN_GROUP</c>); the rule path had no equivalent.
    /// </para>
    /// <para>
    /// Leaving nothing is left to <see cref="SplitCalculator"/> to refuse, which it does by
    /// throwing -- no participants, or weights summing to zero, is not a division it could
    /// carry out. <see cref="ExpenseSplitter"/> turns that into a refusal naming the rule.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SplitWeight> Among(
        IReadOnlyList<SplitWeight> weights, IReadOnlyCollection<Guid> members) =>
        [..weights.Where(weight => members.Contains(weight.UserId))];

    public virtual string? Invalid(TRule rule) =>
        rule.Participants.Select(participant => participant.UserId).Distinct().Count() == rule.Participants.Count
            ? null
            : "A member may appear in a rule only once.";

    public abstract Shared.SplitRuleDto ToDto(TRule rule);

    /// <summary>
    /// The same people with the same weights, in any order. Enough for every proportional
    /// kind, because the weights are the whole of what one says.
    /// </summary>
    public virtual bool SameAs(TRule rule, TRule other)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(other);

        if (rule.Participants.Count != other.Participants.Count)
            return false;

        var theirs = other.Participants.ToDictionary(
            participant => participant.UserId, participant => participant.Weight);

        return rule.Participants.All(participant =>
            theirs.TryGetValue(participant.UserId, out var weight) && weight == participant.Weight);
    }

    protected abstract IReadOnlyList<SplitWeight> WeightsFor(TRule rule, IReadOnlyCollection<Guid> members);

    /// <summary>The participants' weights as they are stored.</summary>
    protected static IReadOnlyList<SplitWeight> StoredWeights(WeightedSplitRuleVersion ruleVersion) =>
        [..ruleVersion.Participants.Select(participant => new SplitWeight(participant.UserId, participant.Weight))];

    /// <summary>Fills a new rule's participants from user-to-weight pairs.</summary>
    protected static TNew Naming<TNew>(TNew rule, IEnumerable<KeyValuePair<Guid, int>> weights)
        where TNew : WeightedSplitRuleVersion
    {
        foreach (var (userId, weight) in weights)
            rule.Participants.Add(new SplitRuleParticipant { UserId = userId, Weight = weight });

        return rule;
    }
}

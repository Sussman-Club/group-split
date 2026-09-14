using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// Everything to one person.
/// </summary>
/// <remarks>
/// The shortest division there is, and the one that shows what the shape costs: no
/// participants, no weights, no arithmetic to speak of.
/// <para>
/// It still goes through <see cref="SplitCalculator"/>, for what looks like no arithmetic at
/// all. Handing it a single weight, and none when the person it names has left, means a rule
/// naming somebody who is gone refuses in exactly the way every proportional rule pruned down
/// to nobody already does -- and <see cref="ExpenseSplitter"/> turns that one refusal into
/// <c>SPLIT_RULE_INVALID</c> naming the rule. Returning the amount regardless would put a
/// share on a person the group's balances no longer list, and the column would stop summing
/// to zero with the missing side belonging to nobody on the page.
/// </para>
/// </remarks>
public class SoleSplitRuleHandler : ISplitRuleHandler<SoleSplitRuleVersion>, ISplitRuleFactory<SoleSplitRuleDto>
{
    public IReadOnlyList<SplitAmount> Divide(
        SoleSplitRuleVersion ruleVersion, SplitRuleContext transaction, IReadOnlyCollection<Guid> members)
    {
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(members);

        return SplitCalculator.Divide(transaction.Amount, transaction.Payer,
            members.Contains(ruleVersion.UserId) ? [new SplitWeight(ruleVersion.UserId, 1)] : []);
    }

    /// <summary>
    /// Whether it says who. That is the whole of what this kind can be wrong about on its
    /// own: whether the person it names is in the group is the service's question, being the
    /// one thing no handler can answer, and it stops being true later anyway when they leave.
    /// </summary>
    public string? Invalid(SoleSplitRuleVersion ruleVersion) =>
        ruleVersion is null || ruleVersion.UserId != Guid.Empty
            ? null
            : "A rule that puts the whole amount on one person has to say which person.";

    public SplitRuleDto ToDto(SoleSplitRuleVersion ruleVersion) =>
        new SoleSplitRuleDto(ruleVersion?.UserId ?? Guid.Empty);

    /// <summary>
    /// The same person. There is nothing else one of these says, and changing the person is
    /// as much an edit as changing a weight.
    /// </summary>
    public bool SameAs(SoleSplitRuleVersion ruleVersion, SoleSplitRuleVersion other) =>
        ruleVersion is not null && other is not null && ruleVersion.UserId == other.UserId;

    public SplitRuleVersion FromDto(SoleSplitRuleDto definition) =>
        new SoleSplitRuleVersion { UserId = definition?.UserId ?? Guid.Empty };
}

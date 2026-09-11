using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// Equally between people, one weight apiece.
/// </summary>
/// <remarks>
/// The only kind that reads the membership. A rule naming nobody divides between whoever is
/// in the group now, which keeps it dividing evenly after somebody joins instead of
/// freezing today's members into weights; naming people narrows it instead. Stored weights
/// are ignored either way -- "even" is what makes it even.
/// <para>
/// Naming people narrows it to those of them who are still participants: the intersection
/// is the base class's, so an old version that still names somebody who has left does not
/// pay them. Naming nobody needs no such trimming -- the membership is where it started.
/// </para>
/// </remarks>
public class EvenSplitRuleHandler
    : WeightedSplitRuleHandler<EvenSplitRuleVersion>, ISplitRuleFactory<EvenSplitRuleDto>
{
    protected override IReadOnlyList<SplitWeight> WeightsFor(
        EvenSplitRuleVersion ruleVersion, IReadOnlyCollection<Guid> members)
    {
        var among = ruleVersion.Participants.Count > 0
            ? ruleVersion.Participants.Select(participant => participant.UserId)
            : members;

        return [..among.Select(userId => new SplitWeight(userId, 1))];
    }

    public override SplitRuleDto ToDto(EvenSplitRuleVersion ruleVersion) =>
        new EvenSplitRuleDto([..ruleVersion.Participants.Select(participant => participant.UserId)]);

    public SplitRuleVersion FromDto(EvenSplitRuleDto definition) =>
        Naming(new EvenSplitRuleVersion(),
            definition.Among.Select(userId => new KeyValuePair<Guid, int>(userId, 1)));
}

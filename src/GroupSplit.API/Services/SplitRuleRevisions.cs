using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// How many rules a membership change rewrote, and how many of them it left naming nobody.
/// </summary>
/// <param name="Emptied">
/// The count is the point. A rule pruned to nothing has changed what it means -- a shares or
/// percentage rule stops dividing and refuses the next expense filed under it, an even one
/// silently becomes "between everybody" -- and the moment it happens is the moment somebody
/// could still be told.
/// </param>
public readonly record struct RuleRewrite(int Rules, int Emptied)
{
    public static readonly RuleRewrite Nothing = new(0, 0);
}

/// <summary>
/// Changes what a group's rules say by opening new versions of them, never by editing the
/// versions they are on.
/// </summary>
public interface ISplitRuleRevisions
{
    /// <summary>
    /// Takes <paramref name="fromUserId"/> out of every rule in the group that currently
    /// names them.
    /// </summary>
    /// <param name="toUserId">
    /// Who takes over their position in the group, or null when nobody does -- which is a
    /// member leaving, where what was theirs is divided among the rest.
    /// </param>
    /// <param name="rules">
    /// What that means for a weight: follow the person, or be dropped. It says nothing about
    /// a rule that puts the whole amount on one person, which has no weight to move and
    /// nothing to be divided among -- one of those follows <paramref name="toUserId"/>
    /// whenever there is one, and is left saying what it says when there is not.
    /// </param>
    Task<RuleRewrite> WithoutParticipant(
        Guid groupId, Guid fromUserId, Guid? toUserId, RuleHandling rules,
        CancellationToken ct = default);
}

/// <summary>
/// The one place a rule changes without somebody editing it.
/// </summary>
/// <remarks>
/// Somebody leaving a group has to come out of the rules that name them, or they keep taking
/// a share of every later expense. What it must <em>not</em> do is reach into the version
/// those rules are on: an expense recorded last March points at that row, and "divide it
/// again by the rule it had at the time" is only true while the row still says what it said
/// in March.
/// <para>
/// So a departure is an edit like any other -- the current version closes, a new one opens
/// without them -- and the rule's history gains an entry saying the group changed shape,
/// which is a better record than the silent deletion it replaces.
/// </para>
/// <para>
/// A version is copied through its own DTO rather than by a clone method per kind. That is
/// the round-trip every rule already survives on the way in from a client, so a kind added
/// later is copyable the moment it is creatable, with nothing here to remember to extend.
/// </para>
/// </remarks>
public sealed class SplitRuleRevisions(
    AppDbContext context,
    ISplitRuleHandler handlers,
    ISplitRuleFactory factory) : ISplitRuleRevisions
{
    public async Task<RuleRewrite> WithoutParticipant(
        Guid groupId, Guid fromUserId, Guid? toUserId, RuleHandling rules,
        CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
            return RuleRewrite.Nothing;

        // Only the versions that are current, and only the ones whose weights name this
        // person. The superseded ones named them too, and that is exactly what they are for.
        //
        // Never the rules the group was given rather than wrote: those stand for one
        // division for as long as they exist, and the member they name is an account, which
        // is anonymised rather than deleted. Nothing in a departure can strand one.
        var affected = await context.Set<WeightedSplitRuleVersion>()
            .Include(version => version.Participants)
            .Where(version => version.SupersededAt == null &&
                              version.SplitRule.Group.Id == groupId &&
                              !version.SplitRule.BuiltIn &&
                              version.Participants.Any(participant => participant.UserId == fromUserId))
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var emptied = 0;

        foreach (var version in affected)
        {
            var replacement = (WeightedSplitRuleVersion)factory.FromDto(handlers.ToDto(version));

            if (Rewrite(replacement, fromUserId, rules is RuleHandling.Transfer ? toUserId : null) == 0)
                emptied++;

            replacement.SplitRuleId = version.SplitRuleId;
            replacement.StartedAt = now;

            version.SupersededAt = now;

            context.Add(replacement);
        }

        return new RuleRewrite(
            affected.Count + await MoveSoleVersions(groupId, fromUserId, toUserId, ct),
            emptied);
    }

    /// <summary>
    /// Points every version that puts the whole amount on the person leaving at whoever
    /// takes their place -- open and closed alike -- and answers with how many rules that
    /// was.
    /// </summary>
    /// <remarks>
    /// The one shape that is rewritten rather than superseded, and the one place a closed
    /// version is ever written. Both follow from what it says: there is no weight to prune
    /// and nobody to prune it among, so a rule of this kind either names the receiver or
    /// names a row that is about to stop existing.
    /// <para>
    /// That row really does stop existing. A stand-in for somebody invited is deleted the
    /// moment the invitation is claimed, declined or withdrawn -- that is what a stand-in is
    /// -- and everything it was holding becomes the receiver's in the same hand-over, share
    /// by share and expense by expense, including on expenses years old. A version left
    /// naming it is a foreign key the delete cannot get past; a version pointed at nobody
    /// would be worse, because the rule's history would stop saying who the money had been
    /// for.
    /// </para>
    /// <para>
    /// So nothing here happens when nobody is named, which is a member leaving. Their
    /// account is anonymised rather than erased, the version goes on naming a row that is
    /// still there, and what the rule said in March is still what it said. What it stops
    /// doing is dividing -- a division only pays people who are still participants, and
    /// <c>ExpenseSplitter</c> says so by name on the next expense filed under it.
    /// </para>
    /// </remarks>
    private async Task<int> MoveSoleVersions(
        Guid groupId, Guid fromUserId, Guid? toUserId, CancellationToken ct)
    {
        if (toUserId is not { } receiverId)
            return 0;

        var naming = await context.Set<SoleSplitRuleVersion>()
            .Where(version => version.SplitRule.Group.Id == groupId &&
                              !version.SplitRule.BuiltIn &&
                              version.UserId == fromUserId)
            .ToListAsync(ct);

        foreach (var version in naming)
            version.UserId = receiverId;

        return naming.Select(version => version.SplitRuleId).Distinct().Count();
    }

    /// <summary>
    /// Takes the departing member out of a copy, and reports how many people it leaves.
    /// </summary>
    /// <remarks>
    /// Weights are added where the receiver already held one, because a rule may hold only
    /// one opinion about a person's weight -- the unique index on (version, user) says so,
    /// and two rows would be the same person counted twice.
    /// </remarks>
    private static int Rewrite(WeightedSplitRuleVersion version, Guid fromUserId, Guid? toUserId)
    {
        var leaving = version.Participants
            .FirstOrDefault(participant => participant.UserId == fromUserId);

        if (leaving is null)
            return version.Participants.Count;

        version.Participants.Remove(leaving);

        if (toUserId is not { } receiverId)
            return version.Participants.Count;

        var held = version.Participants
            .FirstOrDefault(participant => participant.UserId == receiverId);

        if (held is null)
            version.Participants.Add(new SplitRuleParticipant
            {
                UserId = receiverId,
                Weight = leaving.Weight
            });
        else
            held.Weight += leaving.Weight;

        return version.Participants.Count;
    }
}

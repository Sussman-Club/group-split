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
    /// names them -- giving their weight to <paramref name="toUserId"/> when one is named,
    /// and to nobody otherwise.
    /// </summary>
    Task<RuleRewrite> WithoutParticipant(
        Guid groupId, Guid fromUserId, Guid? toUserId, CancellationToken ct = default);
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
        Guid groupId, Guid fromUserId, Guid? toUserId, CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
            return RuleRewrite.Nothing;

        // Only the versions that are current. The superseded ones named this person too,
        // and that is exactly what they are for.
        var affected = await context.Set<SplitRuleVersion>()
            .Include(version => (version as WeightedSplitRuleVersion)!.Participants)
            .Where(version => version.SupersededAt == null &&
                              version.SplitRule.Group.Id == groupId &&
                              (version as WeightedSplitRuleVersion)!.Participants
                              .Any(participant => participant.UserId == fromUserId))
            .ToListAsync(ct);

        if (affected.Count == 0)
            return RuleRewrite.Nothing;

        var now = DateTimeOffset.UtcNow;
        var emptied = 0;

        foreach (var version in affected)
        {
            var replacement = (WeightedSplitRuleVersion)factory.FromDto(handlers.ToDto(version));

            if (Rewrite(replacement, fromUserId, toUserId) == 0)
                emptied++;

            replacement.SplitRuleId = version.SplitRuleId;
            replacement.StartedAt = now;

            version.SupersededAt = now;

            context.Add(replacement);
        }

        return new RuleRewrite(affected.Count, emptied);
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

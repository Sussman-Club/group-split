using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// The rule every group keeps for every member: all of it is for them.
/// </summary>
/// <remarks>
/// It is provisioned rather than written because of what it is for. "This one is not shared
/// -- it is Ana's" is the commonest thing an expense has to say that its category cannot,
/// and a group should not have to invent a rule apiece, name them and keep them in step with
/// its membership before anybody can say it. So the group is given them, one per member, and
/// the expense dialog offers the people rather than a list of rules somebody had to make.
/// <para>
/// Two facts, kept in the two places that state them. <c>SplitRule.BuiltIn</c> says the group
/// was given this rather than writing it, which is why <see cref="SplitRuleService"/> refuses
/// to rename, restate or delete one. Who it is for is not recorded beside it: that is the
/// division, and <c>SplitRuleExtensions.AllFor</c> reads it off the version that states it.
/// </para>
/// </remarks>
public interface IMemberSplitRules
{
    /// <summary>
    /// The rule putting the whole amount on <paramref name="user"/> in this group, created
    /// if the group has not got one yet. Saves.
    /// </summary>
    /// <remarks>
    /// Find-or-create and not create, so every way into a group can call it without knowing
    /// which ways came before: a member who joined by link and then claimed an invitation to
    /// the same group passes through twice, and so does every group the backfill already
    /// covered.
    /// <para>
    /// What it finds is one the group was <em>given</em>, not any rule that happens to divide
    /// that way. A group's own "Ana's gym" says the same thing today and may be restated or
    /// deleted tomorrow, and what the expense dialog offers under Ana's name has to be there
    /// for as long as Ana is.
    /// </para>
    /// </remarks>
    Task<SplitRule> EnsureFor(Group group, User user, CancellationToken ct = default);
}

public sealed class MemberSplitRules(AppDbContext context) : IMemberSplitRules
{
    public async Task<SplitRule> EnsureFor(Group group, User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(user);

        // Asked of the versions, because that is where "who it is for" is written down: a
        // provisioned rule of this group whose open version names this person.
        //
        // As a predicate on the rules rather than a read of the versions themselves, because
        // the answer is a rule with its versions loaded -- and an Include cannot follow a
        // Select that changed what the query is about.
        var existing = await context.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .FirstOrDefaultAsync(rule =>
                rule.BuiltIn &&
                rule.Group.Id == group.Id &&
                rule.Versions.Any(version =>
                    version.SupersededAt == null &&
                    (version as SoleSplitRuleVersion)!.UserId == user.Id), ct);

        if (existing is not null)
            return existing;

        var rule = new SplitRule
        {
            Group = group,
            BuiltIn = true,
            Name = await FreeName(group.Id, user, ct),
            Versions = { new SoleSplitRuleVersion { UserId = user.Id } }
        };

        context.Add(rule);
        await context.SaveChangesAsync(ct);

        return rule;
    }

    /// <summary>
    /// "All for Ana", or the next thing to it that the group has not already used.
    /// </summary>
    /// <remarks>
    /// Names are unique within a group, and nothing stops two members being called Ana or a
    /// group having written a rule called "All for Ana" of its own. Neither is worth refusing
    /// a join over, and neither is worth resolving by making the name unreadable -- who the
    /// rule is for is read off its division, and the app names these by the member rather
    /// than by the name at all -- so the suffix is for the listings that have only the name
    /// to go on.
    /// <para>
    /// It leaves a name that can go stale: somebody who fills their profile in afterwards
    /// keeps the rule they were provisioned under. Renaming it later would mean writing to a
    /// rule the API otherwise refuses to write to, and every screen that shows one of these
    /// has the member in hand.
    /// </para>
    /// </remarks>
    private async Task<string> FreeName(Guid groupId, User user, CancellationToken ct)
    {
        var known = People.Display(user) is { Length: > 0 } display ? display : "one member";

        // 64 is the column, and the suffix has to fit inside it too.
        var stem = Truncated($"All for {known}", 58);

        var taken = await context.Set<SplitRule>()
            .Where(rule => rule.Group.Id == groupId && rule.Name.StartsWith(stem))
            .Select(rule => rule.Name)
            .ToListAsync(ct);

        if (!taken.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return stem;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix})";

            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }
    }

    private static string Truncated(string name, int length) =>
        name.Length <= length ? name : name[..length].TrimEnd();
}

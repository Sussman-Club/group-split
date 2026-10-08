using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    /// <summary>
    /// What becomes of the rule the group holds for <paramref name="fromUserId"/> when their
    /// position passes to <paramref name="toUserId"/>: an invitation claimed, declined or
    /// withdrawn. Does not save.
    /// </summary>
    /// <remarks>
    /// The rule belongs to the position, like its shares and the expenses it paid for, so it
    /// moves with it. Where the receiver has no rule of their own in the group -- somebody
    /// joining by claiming the invitation -- it becomes theirs: every version is repointed,
    /// and the expenses recorded "all for Luz" before she joined now say her account.
    /// <para>
    /// Where the receiver already has one, a second would be two "One pays" entries for one
    /// person, so this one is retired. Nothing divided by it and nothing defaulting to it:
    /// it is deleted. Otherwise it is part of an expense's history and has to stay, so its
    /// versions are repointed at the receiver -- the stand-in they named is about to be
    /// deleted -- and it stops being provisioned: an ordinary rule the group may rename, and
    /// delete once nothing uses it.
    /// </para>
    /// <para>
    /// Called before the receiver's own rule is ensured. On a claim the receiver joins after
    /// this, and finding the moved rule by its division is what stops the join provisioning
    /// a second one; the caller saves in between.
    /// </para>
    /// </remarks>
    Task HandOver(Guid groupId, Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}

public sealed class MemberSplitRules(AppDbContext context) : IMemberSplitRules
{
    public async Task<SplitRule> EnsureFor(Group group, User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(user);

        // A find-or-create with two ways to lose a race, both settled by the name index. Two
        // calls for the same member that read the names before either writes choose the same
        // one, and the loser of IX_SplitRule_GroupId_Name re-reads and finds the winner's
        // rule. Two calls for different members who want the same name collide on it too, and
        // the loser takes the next free name. A few tries, not a loop that assumes the other
        // writers will stop.
        //
        // One rule per person is the service's to keep, not a constraint's: who a rule is for
        // lives on its version, and Postgres cannot index across the two tables. A column on
        // the rule repeating the person was tried and dropped -- it said the same thing
        // twice, and the name already makes a same-member race collide.
        for (var attempt = 1; ; attempt++)
        {
            var existing = await ExistingFor(group.Id, user.Id, ct);

            if (existing is not null)
                return existing;

            var name = await FreeName(group.Id, user, ct);

            // A suffix means the plain name is taken -- and it can be taken by this member's
            // own rule. A call for the same member that commits after the look above and
            // before the names were read leaves this one choosing the next name, which
            // collides with nothing: two rules for one person. The names read saw that
            // commit, so looking again now finds the rule.
            if (name != Stem(user) && await ExistingFor(group.Id, user.Id, ct) is { } written)
                return written;

            var version = new SoleSplitRuleVersion { UserId = user.Id };

            var rule = new SplitRule
            {
                Group = group,
                BuiltIn = true,
                Name = name,
                Versions = { version }
            };

            context.Add(rule);

            try
            {
                await context.SaveChangesAsync(ct);
                return rule;
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException
                      {
                          SqlState: PostgresErrorCodes.UniqueViolation,
                          ConstraintName: "IX_SplitRule_GroupId_Name"
                      })
            {
                // Unlike user provisioning, this context can be carrying a freshly joined
                // member or group that must remain tracked. Drop only the failed insert
                // graph, then see what the concurrent call wrote.
                context.Entry(version).State = EntityState.Detached;
                context.Entry(rule).State = EntityState.Detached;

                // Ours, written by the other call: the next read finds it. Otherwise it was
                // somebody else taking the name first -- two members inviting a "Dani" in the
                // same second -- and the next free name is one read away.
                if (attempt >= 3)
                    throw;
            }
        }
    }

    public async Task HandOver(Guid groupId, Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
            return;

        // None for invitations sent before invitees were given one, and nothing to move.
        var theirs = await ExistingFor(groupId, fromUserId, ct);

        if (theirs is null)
            return;

        var receiverHasOne = await ExistingFor(groupId, toUserId, ct) is not null;

        if (receiverHasOne && !await Referenced(theirs.Id, ct))
        {
            // Loaded with its versions, so the in-memory provider the tests run on deletes
            // them too; on Postgres the cascade does it either way.
            context.Remove(theirs);
            return;
        }

        foreach (var version in theirs.Versions.OfType<SoleSplitRuleVersion>())
            version.UserId = toUserId;

        // Named for whom it is now for. A rule called "All for Luz" that puts everything on
        // Ana -- the absorber, or the account Luz claimed as -- is the name telling a category
        // that defaults to it the wrong thing. Retired beside the receiver's own rule, it
        // takes the next free name after theirs: "All for Ana (2)".
        var receiver = await context.Set<User>().FirstAsync(user => user.Id == toUserId, ct);
        theirs.Name = await FreeName(groupId, receiver, ct, except: theirs.Id);

        // Either it becomes the receiver's own rule -- the repointed version is all that
        // takes -- or, when they already have one and something still divides by this, it is
        // retired: kept, out of the picker, and no longer built in, which is what lets the
        // receiver's stay the one.
        if (receiverHasOne)
            theirs.BuiltIn = false;
    }

    /// <summary>
    /// Whether anything still needs the rule: an expense or a bill line divided by one of its
    /// versions, or a category or the group defaulting to it. The same four things
    /// <c>SplitRuleService.Delete</c> refuses over.
    /// </summary>
    private async Task<bool> Referenced(Guid ruleId, CancellationToken ct) =>
        await context.Set<Transaction>().AnyAsync(transaction => transaction.SplitRuleVersion!.SplitRuleId == ruleId, ct) ||
        await context.Set<ReceiptItem>().AnyAsync(item => item.SplitRuleVersion!.SplitRuleId == ruleId, ct) ||
        await context.Set<Category>().AnyAsync(category => category.DefaultSplitRuleId == ruleId, ct) ||
        await context.Set<Group>().AnyAsync(group => group.DefaultSplitRuleId == ruleId, ct);

    /// <summary>The provisioned rule this group already holds for this member, if any.</summary>
    /// <remarks>
    /// Read off the division: a built-in rule whose open version puts everything on them.
    /// Oldest first, so that if two ever existed the same one is always the answer. With its
    /// versions, which a hand-over rewrites.
    /// </remarks>
    private Task<SplitRule?> ExistingFor(Guid groupId, Guid userId, CancellationToken ct) =>
        context.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .Where(rule =>
                rule.Group.Id == groupId &&
                rule.BuiltIn &&
                rule.Versions.OfType<SoleSplitRuleVersion>()
                    .Any(version => version.SupersededAt == null && version.UserId == userId))
            .OrderBy(rule => rule.Versions.Min(version => version.StartedAt))
            .ThenBy(rule => rule.Id)
            .FirstOrDefaultAsync(ct);

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
    /// has the member in hand. The exception is a hand-over, where the rule changes hands
    /// and the old name would name the wrong person: <paramref name="except"/> lets the rule
    /// being renamed not count its own name as taken.
    /// </para>
    /// </remarks>
    private async Task<string> FreeName(Guid groupId, User user, CancellationToken ct, Guid? except = null)
    {
        var stem = Stem(user);

        var taken = await context.Set<SplitRule>()
            .Where(rule => rule.Group.Id == groupId && rule.Id != except && rule.Name.StartsWith(stem))
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

    /// <summary>The name a member's rule has when nothing else in the group already holds it.</summary>
    private static string Stem(User user)
    {
        var known = People.Display(user) is { Length: > 0 } display ? display : "one member";

        // 64 is the column, and the suffix has to fit inside it too.
        return Truncated($"All for {known}", 58);
    }

    private static string Truncated(string name, int length) =>
        name.Length <= length ? name : name[..length].TrimEnd();
}

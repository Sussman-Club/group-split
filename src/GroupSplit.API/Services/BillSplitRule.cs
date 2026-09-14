using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// The one rule every group keeps for dividing an expense by its own receipt.
/// </summary>
/// <remarks>
/// Provisioned rather than written, for a reason the rule itself makes plain: it holds no
/// settings at all. What it divides by lives on the bill, one line at a time, so two of them
/// are the same rule -- <c>ItemizedSplitRuleHandler.SameAs</c> answers true for any pair --
/// and a group wanting a second is wanting a second name, not a second division.
/// <para>
/// It used to be written, and the names were the whole of what that bought: the seeded Home
/// group held "Dinners out" and "Takeaway", three expenses on the first and thirty-five on
/// the second, dividing identically. The cost was paid by every client, which had to find
/// "the" bill rule among several indistinguishable ones and got it wrong -- reading the first
/// meant an expense on the second matched nothing and fell through to another option
/// entirely. One per group, given rather than made, removes the question.
/// </para>
/// <para>
/// <c>SplitRule.BuiltIn</c> is what says the group was given it, and is why
/// <see cref="SplitRuleService"/> refuses to rename, restate or delete one -- the same
/// protection the per-member rules have.
/// </para>
/// </remarks>
public interface IBillSplitRule
{
    /// <summary>
    /// This group's "divide it by the bill" rule, created if it has not got one. Saves.
    /// </summary>
    /// <remarks>
    /// Find-or-create, so every way into a group can call it without knowing which ways came
    /// before -- the same shape as <see cref="IMemberSplitRules.EnsureFor"/>, and for the same
    /// reason.
    /// </remarks>
    Task<SplitRule> EnsureFor(Group group, CancellationToken ct = default);
}

public sealed class BillSplitRule(AppDbContext context) : IBillSplitRule
{
    /// <summary>What the provisioned one is called, where the name is free.</summary>
    internal const string Name = "Divide by the bill";

    public async Task<SplitRule> EnsureFor(Group group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Asked of the versions, because the kind is the version's and not the rule's. Any
        // itemized rule of this group answers, provisioned or not: a group that wrote one
        // before these were given out keeps it and it becomes the one, rather than ending up
        // beside a second that divides identically.
        var existing = await context.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .FirstOrDefaultAsync(rule =>
                rule.Group.Id == group.Id &&
                rule.Versions.Any(version =>
                    version.SupersededAt == null && version is ItemizedSplitRuleVersion), ct);

        if (existing is not null)
            return existing;

        var rule = new SplitRule
        {
            Group = group,
            BuiltIn = true,
            Name = await FreeName(group.Id, ct),
            Versions = { new ItemizedSplitRuleVersion() }
        };

        context.Add(rule);
        await context.SaveChangesAsync(ct);

        return rule;
    }

    /// <summary>
    /// "Divide by the bill", or the next thing to it the group has not already used.
    /// </summary>
    /// <remarks>
    /// Names are unique within a group and nothing stops a group having written a rule under
    /// this one's name already. Not worth refusing to provision over, and not worth resolving
    /// by making the name unreadable -- every screen labels this option itself rather than
    /// printing the stored name.
    /// </remarks>
    private async Task<string> FreeName(Guid groupId, CancellationToken ct)
    {
        var taken = await context.Set<SplitRule>()
            .Where(rule => rule.Group.Id == groupId && rule.Name.StartsWith(Name))
            .Select(rule => rule.Name)
            .ToListAsync(ct);

        if (!taken.Contains(Name, StringComparer.OrdinalIgnoreCase))
            return Name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{Name} ({suffix})";

            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }
    }
}

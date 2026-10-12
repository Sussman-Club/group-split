using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>The stable, built-in rule for dividing a group's expense evenly.</summary>
public interface IEvenSplitRule
{
    Task<SplitRule> EnsureFor(Group group, CancellationToken ct = default);

    /// <summary>The group's Even rule, or null when it has not been given one yet.</summary>
    /// <remarks>Reads only, for a caller that must not write a rule as a side effect.</remarks>
    Task<SplitRule?> For(Guid groupId, CancellationToken ct = default);
}

/// <remarks>
/// Its empty participant list deliberately means every current member, so the named rule
/// continues to include people who join later.
/// </remarks>
public sealed class EvenSplitRule(AppDbContext context) : IEvenSplitRule
{
    internal const string Name = "Even";

    public async Task<SplitRule> EnsureFor(Group group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var existing = await For(group.Id, ct);

        if (existing is not null)
            return existing;

        var rule = new SplitRule
        {
            Group = group,
            BuiltIn = true,
            Name = await FreeName(group.Id, ct),
            Versions = { new EvenSplitRuleVersion() }
        };

        context.Add(rule);
        await context.SaveChangesAsync(ct);
        return rule;
    }

    public Task<SplitRule?> For(Guid groupId, CancellationToken ct = default) =>
        context.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .FirstOrDefaultAsync(rule => rule.BuiltIn && rule.Group.Id == groupId &&
                rule.Versions.Any(version => version.SupersededAt == null && version is EvenSplitRuleVersion), ct);

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

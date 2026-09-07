using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Every write to a split rule, and the two reads that belong with them. See
/// <see cref="IGroupCommands"/> for why these exist at all.
/// </summary>
/// <remarks>
/// A rule is the division a group keeps -- four ways, or by shares, or all on whoever paid
/// -- and a <see cref="ICategoryCommands">category</see> points at one. The two are written
/// together, so these say nothing to the person: the category names what changed, and a
/// second message about the rule behind it would be the same news twice.
/// <para>
/// They still announce, like every other write. What a rule change moves is the next
/// expense's pre-fill and nothing already recorded, because an expense holds the amounts it
/// was divided into.
/// </para>
/// </remarks>
public interface ISplitRuleCommands
{
    /// <summary>
    /// A group's rules, by name -- what the editor offers to copy a division from.
    /// </summary>
    /// <returns>Null when the read failed.</returns>
    Task<IReadOnlyList<SplitRuleResponse>?> ForGroupAsync(Guid groupId, CancellationToken ct = default);

    /// <summary>One rule with the division it stands for, to edit or to copy.</summary>
    Task<SplitRuleDetailsResponse?> GetAsync(Guid ruleId, CancellationToken ct = default);

    Task<SplitRuleDetailsResponse?> CreateAsync(CreateSplitRuleRequest request, CancellationToken ct = default);

    /// <summary>
    /// Replaces a rule's name and division. A PUT rather than a patch, so the caller hands
    /// over the whole of what the rule is to become.
    /// </summary>
    Task<SplitRuleDetailsResponse?> UpdateAsync(Guid ruleId, UpdateSplitRuleRequest request,
        CancellationToken ct = default);
}

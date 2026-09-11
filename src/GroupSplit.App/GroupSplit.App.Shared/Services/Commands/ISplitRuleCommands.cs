using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Every write to a split rule, and the two reads that belong with them. See
/// <see cref="IGroupCommands"/> for why these exist at all.
/// </summary>
/// <remarks>
/// A rule is the division a group keeps -- four ways, or by shares, or all on whoever paid
/// -- and a <see cref="ICategoryCommands">category</see> points at one.
/// <para>
/// These used to say nothing to the person, because the two were written together in one
/// dialog and the category named what changed. They are edited apart now, on the group's
/// Splits tab: a rule can stand behind several categories, so there is no single category
/// to speak for one, and a save that said nothing would read as a save that did nothing.
/// </para>
/// <para>
/// They announce to the pages as well, like every other write. What a rule change moves is
/// the next expense's pre-fill and nothing already recorded: an expense holds both the
/// amounts it was divided into and the version of the rule that divided them, and editing a
/// rule opens a new version rather than touching either.
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
    /// Every division the rule has stood for, newest first -- what the editor shows so the
    /// person can see that saving adds to a history rather than overwriting one.
    /// </summary>
    Task<SplitRuleHistoryResponse?> HistoryAsync(Guid ruleId, CancellationToken ct = default);

    /// <summary>
    /// Sets a rule's name and the division it stands for from now on. A PUT rather than a
    /// patch, so the caller hands over the whole of what the rule is to become.
    /// </summary>
    /// <remarks>
    /// Adds a version when the division has actually changed, and none when only the name
    /// has. Expenses already recorded keep pointing at the version that divided them.
    /// </remarks>
    Task<SplitRuleDetailsResponse?> UpdateAsync(Guid ruleId, UpdateSplitRuleRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a rule. Categories that divided by it fall back to an even split, and every
    /// expense already recorded keeps the amounts it holds -- including the ones this rule
    /// worked out.
    /// </summary>
    Task<bool> DeleteAsync(Guid ruleId, string name, CancellationToken ct = default);
}

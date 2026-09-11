using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>
/// What divided one expense, as a client can state it: the rule's name, and the window the
/// version that produced the shares stood for.
/// </summary>
/// <param name="VersionId">
/// What the expense records. Null means the amounts are the expense's own -- somebody typed
/// them, or an even division under no rule produced them, which the model does not tell
/// apart.
/// </param>
/// <param name="Version">
/// The version behind <see cref="VersionId"/>, when the rule this expense's category names
/// still has it. Null with a <see cref="VersionId"/> set is the one case worth showing
/// carefully: a rule divided it, and the category has since been pointed somewhere else, so
/// the name on screen would be the wrong one.
/// </param>
/// <param name="Versions">
/// Every version of the rule, newest first, for a caller offering to correct which one an
/// expense records. Empty unless it was asked for.
/// </param>
public sealed record DivisionSourceInfo(
    Guid? VersionId,
    Guid? CategoryId,
    Guid? RuleId,
    string? RuleName,
    SplitRuleVersionResponse? Version,
    IReadOnlyList<SplitRuleVersionResponse> Versions)
{
    /// <summary>The shares are the expense's own, whoever arrived at them.</summary>
    public bool ByHand => VersionId is null;

    /// <summary>A rule produced them, and it is not what the rule says now.</summary>
    public bool Superseded => Version?.SupersededAt is not null;

    /// <summary>A rule produced them and the rule cannot be named from here.</summary>
    public bool RuleUnknown => VersionId is not null && Version is null;

    /// <summary>
    /// Whether the expense is filed under a category at all.
    /// </summary>
    /// <remarks>
    /// Carried because <see cref="RuleUnknown"/> is reached three ways, and the sentence that
    /// fits one of them is a false assertion about the other two -- on the component whose
    /// entire purpose is replacing a guess with a stated fact. The three are told apart by
    /// this and <see cref="RuleId"/>: no category at all; a category that names no rule, or
    /// that has since been deleted; and a category naming a rule whose history does not
    /// contain the version the expense records.
    /// </remarks>
    public bool Filed => CategoryId is not null;

    /// <summary>
    /// Whether a rule was reached at all -- false for a category that names none, and for
    /// one that is no longer there to ask.
    /// </summary>
    public bool Ruled => RuleId is not null;
}

/// <summary>
/// Turns the version id an expense carries into something a person can read.
/// </summary>
/// <remarks>
/// An expense records a split rule <em>version</em> and its category names a <em>rule</em>,
/// and there is no endpoint that goes from the first to the second -- so this walks the way
/// the model is joined: the category says which rule, the rule's history says which window
/// the version stood for.
/// <para>
/// Failures are read and not shown. This describes a figure that is already on screen; a
/// snackbar because the sentence above the shares could not be composed would be noise, and
/// the caller renders nothing in its place.
/// </para>
/// </remarks>
public sealed class DivisionSourceReader(
    ICategoriesClient categories,
    ISplitRulesClient rules,
    ApiErrorPresenter errors)
{
    /// <param name="withHistory">
    /// True when the caller is offering to change which version the expense records, and so
    /// needs every version rather than the one it holds. It costs a read on an expense
    /// nothing divided, which is why it is not the default.
    /// </param>
    /// <returns>
    /// Null when, and only when, a read failed. "Nothing divided it" is an answer and comes
    /// back as a record like any other -- callers rely on that to tell a failure apart from
    /// an expense whose shares are its own, and the distinction is the whole reason a
    /// failure here can afford to stay quiet.
    /// </returns>
    public async Task<DivisionSourceInfo?> ReadAsync(Guid groupId, Guid? categoryId, Guid? versionId,
        bool withHistory = false, CancellationToken ct = default)
    {
        // Nothing divided it and nobody is offering to change that: the answer needs no
        // server at all.
        if (versionId is null && !withHistory)
            return new DivisionSourceInfo(null, categoryId, null, null, null, []);

        Guid? ruleId = null;

        if (categoryId is { } filed)
        {
            var read = await errors.CaptureAsync(async () =>
            {
                var forGroup = await categories.GetCategoriesAsync(groupId, ct);
                ruleId = forGroup?.FirstOrDefault(category => category.Id == filed)?.DefaultSplitRuleId;
            });

            if (read is not null) return null;
        }

        // Filed under nothing, filed under a category that divides evenly by naming no rule,
        // or filed under one that has since been deleted. Any version the expense holds
        // belongs to a rule that cannot be reached from here, which is its own answer.
        if (ruleId is not { } rule)
            return new DivisionSourceInfo(versionId, categoryId, null, null, null, []);

        SplitRuleHistoryResponse? history = null;

        var loaded = await errors.CaptureAsync(async () =>
            history = await rules.GetSplitRuleVersionsAsync(rule, ct));

        if (loaded is not null || history is null) return null;

        IReadOnlyList<SplitRuleVersionResponse> versions = history.Versions is { } chain ? [.. chain] : [];

        var version = versionId is { } id
            ? versions.FirstOrDefault(candidate => candidate.Id == id)
            : null;

        return new DivisionSourceInfo(
            versionId,
            categoryId,
            rule,
            history.Name,
            version,
            withHistory ? versions : []);
    }
}

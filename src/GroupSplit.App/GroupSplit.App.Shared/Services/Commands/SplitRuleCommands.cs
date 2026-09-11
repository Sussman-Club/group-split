using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="ISplitRuleCommands"/>
/// <remarks>
/// No snackbar anywhere in here, which is the one way these differ from the other commands:
/// a rule is written as the division behind a category, in the same gesture, and
/// <see cref="ICategoryCommands"/> says what happened to it. Two messages for one button
/// would be the failure this pattern exists to stop, in the other direction.
/// <para>
/// The announcement is the transactions one, because it is the rule's name that a category
/// row carries and the category's name that every expense row carries. It costs a re-read
/// of the listings for a change that moves no figure; managing rules is a rare enough thing
/// to pay for it, and the alternative is a caller reasoning about whose page cares.
/// </para>
/// </remarks>
public sealed class SplitRuleCommands(
    ISplitRulesClient rules,
    ApiErrorPresenter errors,
    DataChangeNotifier changes) : ISplitRuleCommands
{
    public async Task<IReadOnlyList<SplitRuleResponse>?> ForGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        IReadOnlyList<SplitRuleResponse> loaded = [];

        var done = await errors.TryAsync(async () =>
        {
            loaded = [.. await rules.GetSplitRulesAsync(groupId, ct)];
        }, "Could not load the group's splits.");

        return done ? loaded : null;
    }

    public async Task<SplitRuleDetailsResponse?> GetAsync(Guid ruleId, CancellationToken ct = default)
    {
        SplitRuleDetailsResponse? rule = null;

        var done = await errors.TryAsync(async () => rule = await rules.GetSplitRuleAsync(ruleId, ct),
            "Could not load the split.");

        return done ? rule : null;
    }

    public async Task<SplitRuleHistoryResponse?> HistoryAsync(Guid ruleId, CancellationToken ct = default)
    {
        SplitRuleHistoryResponse? history = null;

        var done = await errors.TryAsync(
            async () => history = await rules.GetSplitRuleVersionsAsync(ruleId, ct),
            "Could not load what this split used to be.");

        return done ? history : null;
    }

    public async Task<SplitRuleDetailsResponse?> CreateAsync(CreateSplitRuleRequest request,
        CancellationToken ct = default)
    {
        SplitRuleDetailsResponse? created = null;

        var done = await errors.TryAsync(async () =>
        {
            created = await rules.CreateSplitRuleAsync(request, ct);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not save the split.");

        return done ? created : null;
    }

    public async Task<SplitRuleDetailsResponse?> UpdateAsync(Guid ruleId, UpdateSplitRuleRequest request,
        CancellationToken ct = default)
    {
        SplitRuleDetailsResponse? updated = null;

        var done = await errors.TryAsync(async () =>
        {
            updated = await rules.UpdateSplitRuleAsync(ruleId, request, ct);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not save the split.");

        return done ? updated : null;
    }
}

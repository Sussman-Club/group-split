using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="ISplitRuleCommands"/>
/// <remarks>
/// The announcement is the transactions one, because it is the rule's name that a category
/// row carries and the category's name that every expense row carries. It costs a re-read
/// of the listings for a change that moves no figure; managing rules is a rare enough thing
/// to pay for it, and the alternative is a caller reasoning about whose page cares.
/// <para>
/// Every message here says the same second thing -- that nothing already recorded moved --
/// because that is the fear a person has when they change how something is divided, and the
/// only way to find out otherwise is to go and look.
/// </para>
/// </remarks>
public sealed class SplitRuleCommands(
    ISplitRulesClient rules,
    ApiErrorPresenter errors,
    ISnackbar snackbar,
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
            snackbar.Add($"{created.Name} created. Point a category at it to use it.", Severity.Success);
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

            // Says the part somebody would otherwise have to test to find out. Editing a
            // division looks like it could reach backwards, and it cannot: the version that
            // was current is closed and a new one opens beside it.
            snackbar.Add($"{updated.Name} updated. Expenses already recorded keep their split.",
                Severity.Success);

            await changes.NotifyTransactionsChangedAsync();
        }, "Could not save the split.");

        return done ? updated : null;
    }

    public Task<bool> DeleteAsync(Guid ruleId, string name, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await rules.DeleteSplitRuleAsync(ruleId, ct);
            snackbar.Add($"{name} deleted.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not delete the split.");
}

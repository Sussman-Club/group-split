using GroupSplit.App.Shared.Localization;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="IRecurringExpenseCommands"/>
/// <remarks>
/// None of these move a balance: the expenses come later, from the server's own job. So none
/// announce a change to the transactions on screen.
/// </remarks>
public sealed class RecurringExpenseCommands(
    IRecurring_expensesClient recurring,
    ApiErrorPresenter errors,
    ISnackbar snackbar) : IRecurringExpenseCommands
{
    public async Task<IReadOnlyList<RecurringExpenseResponse>?> ForGroupAsync(Guid groupId,
        CancellationToken ct = default)
    {
        IReadOnlyList<RecurringExpenseResponse> loaded = [];
        var done = await errors.TryAsync(async () =>
        {
            loaded = [.. await recurring.GetRecurringExpensesAsync(groupId, ct)];
        }, UiText.Get("Could not load the repeating expenses."));

        return done ? loaded : null;
    }

    public async Task<RecurringExpenseResponse?> UpdateAsync(Guid id, RecurringExpenseRequest request,
        CancellationToken ct = default)
    {
        RecurringExpenseResponse? updated = null;
        var done = await errors.TryAsync(async () =>
        {
            updated = await recurring.UpdateRecurringExpenseAsync(id, request, ct);
            snackbar.Add(UiText.Format($"{updated.Details.Name} updated."), Severity.Success);
        }, UiText.Get("Could not save the repeating expense."));

        return done ? updated : null;
    }

    public async Task<RecurringExpenseResponse?> SetPausedAsync(Guid id, bool paused, CancellationToken ct = default)
    {
        RecurringExpenseResponse? changed = null;
        var done = await errors.TryAsync(async () =>
        {
            changed = paused
                ? await recurring.PauseRecurringExpenseAsync(id, ct)
                : await recurring.ResumeRecurringExpenseAsync(id, ct);
        }, UiText.Get("Could not save the repeating expense."));

        return done ? changed : null;
    }

    public async Task<bool> StopAsync(Guid id, CancellationToken ct = default) =>
        await errors.TryAsync(async () =>
        {
            await recurring.StopRecurringExpenseAsync(id, ct);
            snackbar.Add(UiText.Get("Stopped. The expenses it already added stay."), Severity.Success);
        }, UiText.Get("Could not stop the repeating expense."));
}

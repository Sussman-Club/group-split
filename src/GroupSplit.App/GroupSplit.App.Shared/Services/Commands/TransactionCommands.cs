using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="ITransactionCommands"/>
public sealed class TransactionCommands(
    ITransactionsClient transactions,
    ApiErrorPresenter errors,
    ISnackbar snackbar,
    DataChangeNotifier changes) : ITransactionCommands
{
    public Task<bool> CreateAsync(CreateTransactionRequest request, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            var created = await transactions.CreateTransactionAsync(request, ct);

            // Names what happened to what. "Transaction created successfully" was true of
            // every expense anybody ever recorded.
            snackbar.Add(
                created.GroupName is { Length: > 0 } group
                    ? $"{created.Name} added to {group}."
                    : $"{created.Name} added to your personal expenses.",
                Severity.Success);

            await changes.NotifyTransactionsChangedAsync();
        }, "Could not save the expense.");

    public Task<bool> UpdateAsync(Guid transactionId, JsonPatchDocument<UpdateTransactionRequest> patch,
        string name, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await transactions.UpdateTransactionAsync(transactionId, patch, ct);
            snackbar.Add($"{name} updated.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not update the expense.");

    public Task<bool> DeleteAsync(Guid transactionId, string name, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await transactions.DeleteTransactionAsync(transactionId, ct);
            snackbar.Add($"{name} deleted.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not delete the expense.");

    public async Task<SplitPreviewResponse?> PreviewAsync(CreateTransactionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return await transactions.PreviewTransactionSplitsAsync(request, ct);
        }
        catch (Exception exception) when (ApiErrors.IsCancellation(exception) || ApiErrors.IsApiFailure(exception))
        {
            // Null means "no preview to show", and the dialog says so in the space the
            // preview would have taken. A bug still throws: the error boundary is for those.
            return null;
        }
    }
}

using GroupSplit.App.Shared.Components;
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
    IDialogService dialogs,
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

            await OfferBankRowsAsync(created, ct);
        }, "Could not save the expense.");

    /// <summary>
    /// Asks whether what was just recorded is a charge already waiting in the inbox, and
    /// offers to attach it rather than leave the two to be counted twice.
    /// </summary>
    /// <remarks>
    /// Here rather than in the dialogs that record expenses, because there are several of
    /// those and the question is the same from all of them. It runs after the expense is
    /// saved and after the announcement, so nothing about recording an expense waits on it.
    /// <para>
    /// A failure is swallowed. The expense is saved and said so; the same suggestion is
    /// waiting on the row in the inbox, so the worst a failed read costs is that this
    /// particular prompt did not appear.
    /// </para>
    /// </remarks>
    private async Task OfferBankRowsAsync(TransactionResponse created, CancellationToken ct)
    {
        IReadOnlyList<BankTransactionResponse> rows;

        try
        {
            rows = [.. await transactions.GetTransactionBankMatchesAsync(created.Id, ct)];
        }
        catch (Exception exception) when (ApiErrors.IsCancellation(exception) || ApiErrors.IsApiFailure(exception))
        {
            return;
        }

        if (rows.Count == 0)
            return;

        var parameters = new DialogParameters<AttachBankRowDialog>
        {
            { dialog => dialog.Expense, created },
            { dialog => dialog.Rows, rows }
        };

        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true };

        await dialogs.ShowAsync<AttachBankRowDialog>("Your bank already sent this", parameters, options);
    }

    public Task<bool> UpdateAsync(Guid transactionId, JsonPatchDocument<UpdateTransactionRequest> patch,
        string name, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await transactions.UpdateTransactionAsync(transactionId, patch, ct);
            snackbar.Add($"{name} updated.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not update the expense.");

    public Task<bool> DeleteAsync(Guid transactionId, string name, string noun = "expense",
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await transactions.DeleteTransactionAsync(transactionId, ct);
            snackbar.Add($"{name} deleted.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, $"Could not delete the {noun}.");

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

    public async Task<SplitPreviewResponse?> PreviewUpdateAsync(Guid transactionId,
        UpdateTransactionRequest request, bool redivide = false, CancellationToken ct = default)
    {
        try
        {
            // Absent rather than false when nobody asked for it. The endpoint defaults the
            // flag to false, so the two are the same answer -- but a `bool` flowing into
            // the client's `bool?` put `?redivide=false` on every ordinary preview, and a
            // log full of those reads as the app asking for something it is not asking
            // for. The CLI carries the same `? true : null`.
            return await transactions.PreviewUpdatedTransactionSplitsAsync(
                transactionId, request, redivide ? true : null, ct);
        }
        catch (Exception exception) when (ApiErrors.IsCancellation(exception) || ApiErrors.IsApiFailure(exception))
        {
            return null;
        }
    }
}

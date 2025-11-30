using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Transactions;

public class TransactionsService(
    ITransactionsClient client,
    UserTransactionsTracker userTransactionsTracker,
    IDialogService dialog,
    ISnackbar snackbar)
    : ITransactionsService
{
    public ICollection<TransactionResponse>? UserTransactions => userTransactionsTracker.UserTransactions;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        userTransactionsTracker.UserTransactions =
            (await client.GetTransactionsAsync(cancellationToken: ct))?.ToList() ?? [];
    }

    public async Task AddAsync(CreateTransactionRequest request)
    {
        await client.CreateTransactionAsync(request);
        snackbar.Add("Transaction created successfully.", Severity.Success);
        await LoadAsync(); // refresh list
    }

    public async Task EditAsync(Guid id, JsonPatchDocument<UpdateTransactionRequest> patch)
    {
        await client.UpdateTransactionAsync(id, patch);
        snackbar.Add("Transaction updated successfully.", Severity.Success);
        await LoadAsync(); // refresh list
    }

    public async Task DeleteAsync(TransactionResponse transaction)
    {
        await client.DeleteTransactionAsync(transaction.Id);
        snackbar.Add("Transaction deleted successfully.", Severity.Success);
        userTransactionsTracker.UserTransactions?.Remove(transaction); // remove locally
    }

    public async Task<CreateTransactionRequest?> ShowCreateTransactionDialogAsync()
    {
        var parameters = new DialogParameters<CreateTransactionDialog> { { x => x.DisablePayingUserSelection, true } };
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialogReference =
            await dialog.ShowAsync<CreateTransactionDialog>("Create a transaction", parameters, options);

        var dialogResult = await dialogReference.Result;

        if (dialogResult is null || dialogResult.Canceled)
            return null;

        if (dialogResult.Data is not CreateTransactionRequest request)
        {
            snackbar.Add("Invalid transaction.", Severity.Error);
            return null;
        }

        return request;
    }

    public async Task<JsonPatchDocument<UpdateTransactionRequest>?> ShowEditTransactionDialogAsync(
        TransactionResponse transaction)
    {
        var parameters = new DialogParameters<UpdateTransactionDialog> { ["Original"] = transaction };
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialogRef = await dialog.ShowAsync<UpdateTransactionDialog>("Edit Transaction", parameters, options);
        var result = await dialogRef.Result;

        if (result?.Canceled != false) return null;

        if (result.Data is not JsonPatchDocument<UpdateTransactionRequest> patch)
        {
            snackbar.Add("Invalid transaction.", Severity.Error);
            return null;
        }

        return patch;
    }

    public async Task<bool> ShowConfirmDeleteDialogAsync(TransactionResponse transaction)
    {
        var parameters = new DialogParameters<ConfirmationDialog>
        {
            { x => x.ContentText, $"Are you sure you want to delete \"{transaction.Name}\"?" },
            { x => x.ButtonText, "Delete" },
            { x => x.Color, Color.Error }
        };
        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true };
        var dialogRef = await dialog.ShowAsync<ConfirmationDialog>("Delete Transaction", parameters, options);
        var result = await dialogRef.Result;
        return result?.Data as bool? == true;
    }
}
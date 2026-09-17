using GroupSplit.Shared;
using GroupSplit.App.Shared.Services.Errors;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
namespace GroupSplit.App.Shared.Services.Commands;

public sealed class ReceiptCommands(IReceiptsClient receipts, ApiErrorPresenter errors, DataChangeNotifier changes) : IReceiptCommands
{
    private const long MaximumLength = 10 * 1024 * 1024;

    public async Task<ReceiptDraftResponse?> TranscribeAsync(IBrowserFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Size is <= 0 or > MaximumLength)
        {
            await errors.ShowAsync(new ApiError(ApiErrorKind.Validation,
                "Choose a JPG, PNG, WebP, or PDF file no larger than 10 MB."),
                "Could not read receipt file.");
            return null;
        }

        ReceiptDraftResponse? draft = null;
        await using var stream = file.OpenReadStream(MaximumLength, ct);
        var succeeded = await errors.TryAsync(async () =>
                draft = await receipts.TranscribeReceiptDraftAsync(
                    new FileParameter(stream, file.Name, file.ContentType), ct),
            "Could not read receipt file.");

        return succeeded ? draft : null;
    }

    public async Task<ReceiptResponse?> GetAsync(Guid transactionId, CancellationToken ct = default)
    {
        try { return await receipts.GetReceiptAsync(transactionId, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
    public async Task<ReceiptResponse?> SaveAsync(Guid transactionId, SaveReceiptRequest request, CancellationToken ct = default)
    {
        ReceiptResponse? result = null;
        await errors.TryAsync(async () =>
        {
            result = await receipts.SaveReceiptAsync(transactionId, request, ct);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not save the receipt.");
        return result;
    }

    public async Task<ReceiptResponse?> PatchItemAsync(Guid transactionId, Guid itemId,
        JsonPatchDocument<ReceiptItemPatch> patch, CancellationToken ct = default)
    {
        ReceiptResponse? result = null;
        await errors.TryAsync(async () =>
        {
            result = await receipts.PatchReceiptItemAsync(transactionId, itemId, patch, ct);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not save the receipt item.");
        return result;
    }
    public async Task<ReceiptResponse?> SetRuleAsync(Guid transactionId, Guid itemId, Guid? versionId)
    {
        ReceiptResponse? result = null;
        await errors.TryAsync(async () => result = await receipts.SetReceiptItemRuleAsync(transactionId, itemId,
            new SetReceiptItemRuleRequest { SplitRuleVersionId = versionId }), "Could not change the item's rule.");
        return result;
    }
    public async Task<ReceiptDivisionResponse?> PreviewAsync(Guid transactionId)
    {
        ReceiptDivisionResponse? result = null;
        await errors.TryAsync(async () => result = await receipts.PreviewReceiptDivisionAsync(transactionId),
            "Could not preview the item split.");
        return result;
    }
    public Task<bool> DivideAsync(Guid transactionId) => errors.TryAsync(async () =>
    {
        await receipts.DivideByReceiptAsync(transactionId);
        await changes.NotifyTransactionsChangedAsync();
    }, "Could not divide the expense.");
}

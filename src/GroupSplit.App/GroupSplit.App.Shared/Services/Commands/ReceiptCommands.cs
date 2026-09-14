using GroupSplit.Shared;
using GroupSplit.App.Shared.Services.Errors;
namespace GroupSplit.App.Shared.Services.Commands;

public sealed class ReceiptCommands(IReceiptsClient receipts, ApiErrorPresenter errors, DataChangeNotifier changes) : IReceiptCommands
{
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

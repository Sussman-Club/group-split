using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
namespace GroupSplit.App.Shared.Services.Commands;

public interface IReceiptCommands
{
    Task<ReceiptDraftResponse?> TranscribeAsync(IBrowserFile file, CancellationToken ct = default);
    Task<ReceiptResponse?> GetAsync(Guid transactionId, CancellationToken ct = default);
    Task<ReceiptResponse?> SaveAsync(Guid transactionId, SaveReceiptRequest request, CancellationToken ct = default);
    Task<ReceiptResponse?> PatchItemAsync(Guid transactionId, Guid itemId,
        JsonPatchDocument<ReceiptItemPatch> patch, CancellationToken ct = default);
    Task<ReceiptResponse?> SetRuleAsync(Guid transactionId, Guid itemId, Guid? versionId);
    Task<ReceiptDivisionResponse?> PreviewAsync(Guid transactionId);
    Task<bool> DivideAsync(Guid transactionId);
}

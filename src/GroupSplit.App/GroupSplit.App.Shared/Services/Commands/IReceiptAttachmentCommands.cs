using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace GroupSplit.App.Shared.Services.Commands;

public interface IReceiptAttachmentCommands
{
    Task<IReadOnlyList<ReceiptAttachmentResponse>> GetAsync(Guid transactionId, CancellationToken ct = default);
    Task<ReceiptAttachmentResponse?> UploadAsync(Guid transactionId, IBrowserFile file, CancellationToken ct = default);
}

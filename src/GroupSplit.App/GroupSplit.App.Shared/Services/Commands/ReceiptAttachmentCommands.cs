using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace GroupSplit.App.Shared.Services.Commands;

public sealed class ReceiptAttachmentCommands(IReceiptsClient receipts, ApiErrorPresenter errors) : IReceiptAttachmentCommands
{
    private const long MaximumLength = 10 * 1024 * 1024;

    public async Task<IReadOnlyList<ReceiptAttachmentResponse>> GetAsync(Guid transactionId, CancellationToken ct = default)
    {
        List<ReceiptAttachmentResponse> attachments = [];
        var loaded = await errors.TryAsync(async () =>
            attachments = (await receipts.GetReceiptAttachmentsAsync(transactionId, ct)).ToList(),
            "Could not load receipt files.");

        return loaded ? attachments : [];
    }

    public async Task<ReceiptAttachmentResponse?> UploadAsync(Guid transactionId, IBrowserFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Size is <= 0 or > MaximumLength)
        {
            await errors.ShowAsync(new ApiError(ApiErrorKind.Validation,
                "Choose a JPG, PNG, WebP, or PDF file no larger than 10 MB."), "Could not upload receipt file.");
            return null;
        }

        ReceiptAttachmentResponse? uploaded = null;
        await using var stream = file.OpenReadStream(MaximumLength, ct);
        var succeeded = await errors.TryAsync(async () =>
                uploaded = await receipts.UploadReceiptAttachmentAsync(transactionId,
                    new FileParameter(stream, file.Name, file.ContentType), ct),
            "Could not upload receipt file.");

        return succeeded ? uploaded : null;
    }
}

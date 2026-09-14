using GroupSplit.API.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>The provider-neutral part of reading a receipt attachment into an editable bill.</summary>
public interface IReceiptTranscriptionService
{
    Task<ReceiptTranscriptionResponse> Transcribe(Guid expenseId, Guid attachmentId, CancellationToken ct = default);
}

/// <summary>
/// The boundary around a document-understanding provider. Implementations know their own
/// wire protocol; nothing outside this interface needs to know whether the first provider
/// is Veryfi or a later replacement.
/// </summary>
public interface IReceiptTranscriptionProvider
{
    string Name { get; }
    Task<TranscribedReceipt> Transcribe(ReceiptSourceDocument document, CancellationToken ct = default);
}

/// <summary>The private file passed to a provider, without leaking storage implementation details.</summary>
public sealed record ReceiptSourceDocument(
    Guid AttachmentId,
    string FileName,
    string ContentType,
    byte[] Content);

/// <summary>The normalized output every provider must map its response into.</summary>
public sealed record TranscribedReceipt(
    decimal Subtotal,
    decimal Tax,
    decimal Tip,
    decimal Total,
    IReadOnlyList<TranscribedReceiptItem> Items);

public sealed record TranscribedReceiptItem(
    string Name,
    decimal UnitPrice,
    decimal Quantity,
    decimal TotalPrice,
    decimal TaxAmount)
{
    public string? NormalizedName { get; init; }
    public string? Description { get; init; }
}

public sealed class ReceiptTranscriptionService(
    IReceiptAttachmentService attachments,
    IReceiptTranscriptionProvider provider) : IReceiptTranscriptionService
{
    public async Task<ReceiptTranscriptionResponse> Transcribe(
        Guid expenseId, Guid attachmentId, CancellationToken ct = default)
    {
        var source = await attachments.Download(expenseId, attachmentId, ct);
        var transcription = await provider.Transcribe(new ReceiptSourceDocument(
            attachmentId, source.FileName, source.ContentType, source.Content), ct);

        return new ReceiptTranscriptionResponse(attachmentId, provider.Name, new SaveReceiptRequest
        {
            Subtotal = transcription.Subtotal,
            Tax = transcription.Tax,
            Tip = transcription.Tip,
            Total = transcription.Total,
            Items = transcription.Items.Select(item => new ReceiptItemInput
            {
                Name = item.Name,
                NormalizedName = item.NormalizedName ?? item.Name.Trim().ToLowerInvariant(),
                Description = item.Description,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                TotalPrice = item.TotalPrice,
                TaxAmount = item.TaxAmount
            }).ToArray()
        });
    }
}

/// <summary>Lets the API start without OCR credentials while making the missing setup explicit.</summary>
internal sealed class UnavailableReceiptTranscriptionProvider : IReceiptTranscriptionProvider
{
    public string Name => "unconfigured";

    public Task<TranscribedReceipt> Transcribe(ReceiptSourceDocument document, CancellationToken ct = default) =>
        throw new ConflictException(ErrorCodes.ReceiptTranscriptionUnavailable,
            "Receipt transcription is not configured for this deployment.");
}

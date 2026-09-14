namespace GroupSplit.Shared;

/// <summary>Metadata for a private source file attached to an expense.</summary>
public sealed record ReceiptAttachmentResponse(
    Guid Id,
    Guid ExpenseId,
    Guid? ReceiptId,
    string FileName,
    string ContentType,
    long Length,
    DateTimeOffset UploadedAt);

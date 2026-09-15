namespace GroupSplit.Shared;

/// <summary>Metadata for a private source file attached to a pending bank row or expense.</summary>
public sealed record ReceiptAttachmentResponse(
    Guid Id,
    Guid? ExpenseId,
    Guid? BankTransactionId,
    Guid? ReceiptId,
    string FileName,
    string ContentType,
    long Length,
    DateTimeOffset UploadedAt);

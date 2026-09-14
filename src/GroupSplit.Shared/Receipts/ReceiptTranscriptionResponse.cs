namespace GroupSplit.Shared;

/// <summary>
/// A receipt draft extracted from a source attachment. It has not been saved: OCR is a
/// suggestion, and the person recording the expense still assigns each line's split rule.
/// </summary>
public sealed record ReceiptTranscriptionResponse(
    Guid AttachmentId,
    string Provider,
    SaveReceiptRequest Receipt);

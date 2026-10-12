namespace GroupSplit.Shared;

/// <summary>
/// A receipt draft extracted from a source attachment. It has not been saved: OCR is a
/// suggestion. Its lines carry no rule; saved, they start on the expense category's, and the
/// person recording the expense changes the ones that divide differently.
/// </summary>
public sealed record ReceiptTranscriptionResponse(
    Guid AttachmentId,
    string Provider,
    SaveReceiptRequest Receipt);

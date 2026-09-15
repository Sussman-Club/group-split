namespace GroupSplit.Shared;

/// <summary>
/// A receipt read before it belongs to an expense. The values are suggestions: the caller
/// reviews them before creating the expense and saving the bill.
/// </summary>
public sealed record ReceiptDraftResponse(
    string Provider,
    SaveReceiptRequest Receipt);

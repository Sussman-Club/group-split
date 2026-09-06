namespace GroupSplit.Shared;

/// <summary>
/// What an expense would be divided into if it were saved as described -- worked out by the
/// same code that would save it, and saving nothing.
/// </summary>
/// <remarks>
/// The dialog could divide evenly itself, and used to, in a starting point that was allowed
/// to be approximate because a person was about to edit it. A preview cannot be
/// approximate: it is shown as what will happen, so anything the client re-derives is a
/// second copy of the money arithmetic that has to agree with the first to the cent. This
/// asks instead.
/// </remarks>
/// <param name="RuleName">
/// The split rule the category pointed at, or null when there was none and the amount was
/// divided evenly. Named so the dialog can say why the numbers are what they are.
/// </param>
public record SplitPreviewResponse(IReadOnlyList<TransactionSplitResponse> Splits, string? RuleName);

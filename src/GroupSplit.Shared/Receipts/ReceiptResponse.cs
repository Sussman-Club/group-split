namespace GroupSplit.Shared;

/// <summary>
/// A bill as a screen needs it: the paper, plus the two things only the server can say about
/// it -- whether dividing by it would work right now, and whether the expense divides by it
/// at all or merely has one attached.
/// </summary>
/// <remarks>
/// <see cref="MissingRuleItemCount"/> is sent as a count so a screen can say "2 lines need a
/// rule" without walking the list, and <see cref="CanDivide"/> is computed by attempting the
/// division rather than by a second set of checks: a button that offers itself and then
/// refuses is worse than one that never offered.
/// </remarks>
public sealed record ReceiptResponse(
    Guid Id, Guid ExpenseId, decimal Subtotal, decimal Tax, decimal Tip, decimal Total,
    int MissingRuleItemCount, bool CanDivide, bool DividesItsExpense,
    IReadOnlyList<ReceiptItemResponse> Items)
{
    public bool CanEdit { get; init; }
}

/// <summary>
/// One line, carrying its rule three ways: the version id to send back, the rule's name to
/// print, and the definition so a screen can say what that rule actually does.
/// </summary>
public sealed record ReceiptItemResponse(
    Guid Id, string Name, decimal UnitPrice, decimal Quantity, decimal TotalPrice,
    decimal TaxAmount, Guid? SplitRuleVersionId, string? SplitRuleName, SplitRuleDto? SplitRule)
{
    public string NormalizedName { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed record ReceiptDivisionResponse(
    Guid ReceiptId, decimal Total, IReadOnlyList<ReceiptShareResponse> Shares);

/// <summary>
/// What one person owes, and how much of that was food: the two figures side by side are
/// what makes the apportioning of tax and tip checkable rather than something to take on
/// trust.
/// </summary>
public sealed record ReceiptShareResponse(Guid UserId, decimal Subtotal, decimal Amount);

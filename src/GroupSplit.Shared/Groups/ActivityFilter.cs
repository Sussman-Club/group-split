namespace GroupSplit.Shared;

/// <summary>
/// What a group's ledger is narrowed to. Every member is optional, and an absent one
/// narrows nothing.
/// </summary>
/// <remarks>
/// The ledger is the Expenses and Activity tabs merged, so the thing that used to be a
/// choice of tab is <see cref="Kind"/> here -- in the query string, which is what makes a
/// filtered ledger a place somebody can be sent to rather than a mode they have to set.
/// <para>
/// The figures above the list are read with the same filter, except for the total, which
/// counts expenses whatever <see cref="Kind"/> says. That is deliberate and it is what
/// makes one tab safe: a settlement is money changing hands, not money spent, and a total
/// that mixed them would be wrong in a way nobody would catch.
/// </para>
/// </remarks>
/// <param name="Kind">
/// Expenses only, settlements only, or -- absent -- everything, which is the default
/// because "what has happened here" does not distinguish.
/// </param>
/// <param name="Search">
/// Free text, matched anywhere in the name, the note, the category, or either party's
/// name. Server-side, because what is on screen is one page.
/// </param>
/// <param name="EnteredBy">Only what this person recorded, whoever paid it.</param>
/// <param name="Source">Only what came in this way: typed in, filed from a bank row or a receipt, or through MCP.</param>
public record ActivityFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    ActivityKind? Kind = null,
    string? Search = null,
    Guid? CategoryId = null,
    bool? Uncategorized = null,
    Guid? PaidBy = null,
    Guid? EnteredBy = null,
    TransactionSource? Source = null);

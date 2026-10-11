namespace GroupSplit.Shared;

/// <summary>
/// What a listing of expenses is narrowed to. Every member is optional, and an absent one
/// narrows nothing; the same filter reads a page and its summary, so the figures beside a
/// page always describe the whole match rather than the rows in hand.
/// </summary>
/// <param name="From">Inclusive lower bound on the date.</param>
/// <param name="To">Inclusive upper bound on the date.</param>
/// <param name="Category">The category exactly, matched without regard to case.</param>
/// <param name="Personal">
/// Whether to keep only the caller's own expenses -- the ones in their personal group --
/// or only the shared ones. Null keeps both, which is what a listing means when nobody has
/// said otherwise. The personal group can also be named as <c>GroupId</c>, since its id is
/// the caller's; this says "personal or not" without asking anybody for an id.
/// </param>
/// <param name="Search">
/// Free text, matched anywhere in the name, description or category, the group's name, or
/// the payer's. This is the search box, and it belongs here rather than in the client: a
/// client that filters what it was given can only search the page it holds.
/// </param>
public record TransactionFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? GroupId = null,
    Guid? PaidByUserId = null,
    string? Category = null,
    bool? Personal = null,
    string? Search = null);

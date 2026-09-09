namespace GroupSplit.Shared;

/// <summary>What kind of thing happened in a group.</summary>
public enum ActivityKind
{
    /// <summary>Something the group spent.</summary>
    Expense = 0,

    /// <summary>One member paying another back.</summary>
    Transfer = 1
}

/// <summary>
/// One thing that happened in a group, whichever kind it was.
/// </summary>
/// <remarks>
/// The one listing that deliberately shows transfers. Every other expense surface reads
/// <c>Set&lt;Expense&gt;()</c> and therefore cannot see them, which is the whole point of
/// the reshape -- but a group's history is not only its spending, and "Omar paid you 40"
/// is the row people look for when the balance moves and they want to know why.
/// </remarks>
public record GroupActivityResponse
{
    public Guid Id { get; init; }

    public ActivityKind Kind { get; init; }

    public string Name { get; init; } = "";

    public string? Description { get; init; }

    public decimal Amount { get; init; }

    public DateTimeOffset DateTime { get; init; }

    /// <summary>Who paid: the person who spent it, or the person who paid somebody back.</summary>
    public Guid PaidByUserId { get; init; }

    public string PaidByUserName { get; init; } = "";

    /// <summary>
    /// Who received it, on a transfer, and null on an expense -- where the money went to a
    /// shop rather than to a member, and the shares say who carried it.
    /// </summary>
    public Guid? PaidToUserId { get; init; }

    public string? PaidToUserName { get; init; }

    /// <summary>What the expense was filed under, when it was an expense filed under anything.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Where it was spent, for an expense filed from a bank row that named a place. Null on
    /// a transfer and on anything somebody typed in: those went to a person, or to a shop
    /// nobody wrote down.
    /// </summary>
    public string? MerchantName { get; init; }

    /// <summary>
    /// The merchant's logo, where there is one. Read through the merchant and not stored
    /// per row, so a logo that arrives later arrives on every expense at that place at once.
    /// </summary>
    public string? MerchantLogoUrl { get; init; }

    /// <summary>
    /// What this cost the caller: their split of an expense, or null on a transfer.
    /// </summary>
    /// <remarks>
    /// Null rather than zero, and the two are different answers. A transfer has no share --
    /// paying somebody back is not a cost anyone carries a part of -- and a row that said
    /// <c>0.00</c> would be claiming the caller's part of it was nothing. It renders as a
    /// dash for that reason.
    /// <para>
    /// What the dinner cost and what it cost you are different numbers, and every listing
    /// in the app used to show only the first.
    /// </para>
    /// </remarks>
    public decimal? Share { get; init; }
}

/// <summary>
/// The wire type for a page of activity. Exists for the same reason
/// <see cref="PagedResponseOfTransactionResponse"/> does -- see the note there.
/// </summary>
public sealed record PagedResponseOfGroupActivityResponse(
    IReadOnlyList<GroupActivityResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<GroupActivityResponse>(Items, Page, PageSize, TotalCount);

/// <summary>
/// One row of a group's ledger: the entry, plus the two figures that only mean anything
/// to the person reading it.
/// </summary>
/// <remarks>
/// The ledger is the Expenses and Activity tabs merged, and <see cref="RunningBalance"/>
/// is the reason to merge them rather than simply rename one: a balance history cannot be
/// drawn against a list that hides half the events that move it.
/// </remarks>
public record GroupLedgerEntryResponse : GroupActivityResponse
{
    /// <summary>
    /// Where the caller stood in this group immediately after this entry, oldest to newest.
    /// Positive when the group owes them.
    /// </summary>
    /// <remarks>
    /// Cumulative over the whole ledger and not over the page in hand, and it ignores the
    /// filters: a balance as at a date is what it is whether or not the rows above it are
    /// currently on screen. Narrowing the list to settlements does not rewrite history.
    /// </remarks>
    public decimal RunningBalance { get; init; }
}

/// <summary>
/// The wire type for a page of ledger entries. Exists for the same reason
/// <see cref="PagedResponseOfTransactionResponse"/> does -- see the note there.
/// </summary>
public sealed record PagedResponseOfGroupLedgerEntryResponse(
    IReadOnlyList<GroupLedgerEntryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<GroupLedgerEntryResponse>(Items, Page, PageSize, TotalCount);

/// <summary>
/// One thing that happened in any of the caller's groups, or outside all of them.
/// </summary>
/// <remarks>
/// The home page's feed. A group's activity answers "what has happened here"; this answers
/// "what has happened anywhere", which is the question somebody opening the app has, and
/// which used to be answerable only as "expenses you personally paid for" -- the one slice
/// of it that was already a listing.
/// </remarks>
public record UserActivityResponse : GroupActivityResponse
{
    /// <summary>
    /// The group it happened in, or null when it is the caller's own. Null is not a missing
    /// value: an expense with no group is what personal means.
    /// </summary>
    public Guid? GroupId { get; init; }

    public string? GroupName { get; init; }
}

/// <summary>
/// The wire type for a page of cross-group activity. Exists for the same reason
/// <see cref="PagedResponseOfTransactionResponse"/> does -- see the note there.
/// </summary>
public sealed record PagedResponseOfUserActivityResponse(
    IReadOnlyList<UserActivityResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<UserActivityResponse>(Items, Page, PageSize, TotalCount);

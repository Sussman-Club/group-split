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

namespace GroupSplit.Shared;

/// <summary>
/// One repayment the caller was party to, in whichever group it was recorded.
/// </summary>
/// <remarks>
/// The caller's settlement history across every group, which until now could only be
/// assembled by opening each group's activity in turn. It is what answers "did I already
/// pay this?", and that question is the thing that stops people settling twice.
/// <para>
/// Every row has the caller on one end. A transfer between two other members belongs to
/// the group's own history, not to this one.
/// </para>
/// </remarks>
public record SettlementResponse
{
    public Guid Id { get; init; }

    public Guid GroupId { get; init; }

    public string GroupName { get; init; } = "";

    public Guid FromUserId { get; init; }

    public string FromUserName { get; init; } = "";

    public Guid ToUserId { get; init; }

    public string ToUserName { get; init; } = "";

    public decimal Amount { get; init; }

    public DateTimeOffset DateTime { get; init; }

    /// <summary>What the payer wanted remembered, when they said -- "cash", "end of August".</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the caller was the payer. Derivable from <see cref="FromUserId"/> by anyone
    /// who knows their own id, and stated because it is what the row's wording turns on:
    /// "You paid Daniel" and "Omar paid you" are the same record read from two ends.
    /// </summary>
    public bool PaidByYou { get; init; }
}

/// <summary>
/// The wire type for a page of settlements. Exists for the same reason
/// <see cref="PagedResponseOfTransactionResponse"/> does -- see the note there.
/// </summary>
public sealed record PagedResponseOfSettlementResponse(
    IReadOnlyList<SettlementResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<SettlementResponse>(Items, Page, PageSize, TotalCount);

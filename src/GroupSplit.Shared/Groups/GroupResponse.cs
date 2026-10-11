namespace GroupSplit.Shared;

public record GroupResponse(Guid Id, string Name, int MemberCount, bool IsArchive = false)
{
    public string Currency { get; init; } = "USD";
    public Guid? DefaultSplitRuleId { get; init; }
    public string? DefaultSplitRuleName { get; init; }

    /// <summary>The span the group's Activity tab opens on.</summary>
    public ActivityPeriod DefaultPeriod { get; init; } = ActivityPeriod.ThisMonth;

    /// <summary>
    /// Whether this is the caller's personal group: theirs alone, with no members, rules or
    /// settlements to offer. Its id is the caller's own user id.
    /// </summary>
    public bool IsPersonal { get; init; }
}

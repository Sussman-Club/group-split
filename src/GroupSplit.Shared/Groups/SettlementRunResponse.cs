namespace GroupSplit.Shared;

/// <summary>
/// One payment in a settling-up: who hands money to whom, and how much.
/// </summary>
/// <remarks>
/// Gross and one-directional. Two people who each owe the other are one payment here, not
/// two, which is the whole point of minimising: the fewest payments that leave everybody
/// square.
/// </remarks>
public record SettlementPayment
{
    public Guid FromUserId { get; init; }

    public string FromUserName { get; init; } = "";

    public Guid ToUserId { get; init; }

    public string ToUserName { get; init; } = "";

    public decimal Amount { get; init; }
}

/// <summary>
/// What a settling-up would do, worked out and handed back without recording anything.
/// </summary>
/// <remarks>
/// Separate from running it because the payments are the part people want to see before
/// they agree to them, and because a scope that turns out to match nothing should say so
/// rather than write an empty run.
/// </remarks>
public record SettleUpPreviewResponse
{
    /// <summary>Who pays whom. Empty when the scope is already square.</summary>
    public IReadOnlyList<SettlementPayment> Payments { get; init; } = [];

    /// <summary>
    /// Where each member stands over the swept set alone -- not their standing balance,
    /// which is over everything outstanding.
    /// </summary>
    public IReadOnlyList<GroupNetBalance> Balances { get; init; } = [];

    /// <summary>How many transactions the scope matched.</summary>
    public int TransactionCount { get; init; }

    /// <summary>What they came to, added up.</summary>
    public decimal Total { get; init; }

    /// <summary>
    /// The dates actually covered, which are the swept rows' own and not the scope's: a
    /// scope with no end date still covers a definite stretch, and that is the stretch
    /// worth showing. Null when nothing matched.
    /// </summary>
    public DateTimeOffset? CoversFrom { get; init; }

    public DateTimeOffset? CoversTo { get; init; }

    /// <summary>
    /// What the run would be called if nobody names it. Derived from the dates covered, so
    /// a month's worth of expenses proposes the month.
    /// </summary>
    public string SuggestedLabel { get; init; } = "";
}

/// <summary>
/// A settling-up that happened.
/// </summary>
public record SettlementRunResponse
{
    public Guid Id { get; init; }

    public string Label { get; init; } = "";

    public DateTimeOffset RanAt { get; init; }

    public DateTimeOffset EffectiveDate { get; init; }

    /// <summary>
    /// When it was undone, or null while it still stands. Undoing puts everything it settled
    /// back to outstanding without taking the payments back, so a reopened run is history
    /// rather than a mistake.
    /// </summary>
    public DateTimeOffset? ReopenedAt { get; init; }

    public Guid? RanByUserId { get; init; }

    public string? RanByUserName { get; init; }

    /// <summary>
    /// The payments it wrote. Not the repayments it merely settled -- somebody's earlier
    /// hand-recorded 30 is money this run counted, not money it moved.
    /// </summary>
    public IReadOnlyList<SettlementPayment> Payments { get; init; } = [];

    /// <summary>
    /// How many transactions it swept, counting the payments it wrote -- they are part of
    /// what it settled, which is what leaves the group square afterwards. Zero once it has
    /// been reopened, since it is then holding nothing.
    /// </summary>
    public int TransactionCount { get; init; }

    /// <summary>What the spending it settled came to, leaving repayments out of it.</summary>
    public decimal Total { get; init; }

    public DateTimeOffset? CoversFrom { get; init; }

    public DateTimeOffset? CoversTo { get; init; }
}

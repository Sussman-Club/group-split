namespace GroupSplit.Shared;

/// <summary>
/// One payment in a settling-up: who hands money to whom, and how much.
/// </summary>
/// <remarks>
/// Gross and one-directional, and the caller is always one of the two ends. Two people who
/// each owe the other are one payment here rather than two, which is what minimising the
/// group's debts is for: the fewest payments that leave everybody square.
/// </remarks>
public record SettlementPayment
{
    public Guid FromUserId { get; init; }

    /// <summary>Empty when the payer is the caller, who does not need naming to themselves.</summary>
    public string FromUserName { get; init; } = "";

    public Guid ToUserId { get; init; }

    public string ToUserName { get; init; } = "";

    public decimal Amount { get; init; }
}

/// <summary>
/// What a settling-up recorded.
/// </summary>
public record SettleUpResponse
{
    /// <summary>Every repayment it wrote, in both directions.</summary>
    public IReadOnlyList<SettlementPayment> Payments { get; init; } = [];

    /// <summary>The date they all carry.</summary>
    public DateTimeOffset Date { get; init; }

    /// <summary>The note they all carry, when there was one.</summary>
    public string? Description { get; init; }

    /// <summary>What changed hands in total, both directions added up.</summary>
    public decimal Total => Payments.Sum(payment => payment.Amount);
}

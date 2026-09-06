namespace GroupSplit.API.Services;

/// <summary>
/// One participant's weight in a split. What the weight means is the rule's business --
/// shares, hundredths of a percent, or one apiece for an even split -- because the
/// arithmetic is the same either way: a share of the total in proportion to the weight.
/// </summary>
public readonly record struct SplitWeight(Guid UserId, int Weight);

/// <summary>
/// What one participant owes on one transaction.
/// </summary>
public readonly record struct SplitAmount(Guid UserId, decimal Amount);

/// <summary>
/// Divides an amount between participants so that the parts sum to the whole.
/// </summary>
/// <remarks>
/// This is the only place the split arithmetic is written. It used to be written three
/// times -- in the LINQ that had to survive translation into the balance SQL, in memory
/// when a transaction's details were read, and a third time converting shares to
/// percentages -- and the three did not agree. The first two truncated each non-payer's
/// share and left the payer the remainder; the third rounded, and gave the drift to
/// whichever participant a dictionary happened to enumerate last, which is not a rule
/// anyone chose.
/// <para>
/// Now it runs once, at write time, and its output is stored as rows. The balance query
/// sums those rows and has no arithmetic of its own left to disagree with.
/// </para>
/// </remarks>
public static class SplitCalculator
{
    /// <summary>
    /// Divides <paramref name="amount"/> between <paramref name="weights"/> in proportion,
    /// truncated to the cent, with the remainder given to one participant so that the
    /// result sums to <paramref name="amount"/> exactly.
    /// </summary>
    /// <param name="payerId">
    /// Who paid. They absorb the remainder when they are a participant, which is the
    /// common case and the one people expect: the person who put the money down carries
    /// the fraction of a cent nobody else can.
    /// </param>
    /// <remarks>
    /// Truncation rather than rounding, so no participant is ever charged more than their
    /// proportion; the remainder is always the payer's to carry, never somebody else's to
    /// discover.
    /// <para>
    /// When the payer is not among the participants -- they paid for a split they are not
    /// part of -- the remainder goes to the largest weight, ties broken by the smaller id.
    /// That is arbitrary, but it is fixed, and being fixed is the whole point: the same
    /// inputs give the same answer on every machine and in every enumeration order.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// No participants, a participant named twice, a negative weight, or weights summing
    /// to zero -- none of which describe a split that could be stored.
    /// </exception>
    public static IReadOnlyList<SplitAmount> Divide(
        decimal amount,
        Guid payerId,
        IReadOnlyList<SplitWeight> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        if (weights.Count == 0)
            throw new ArgumentException("A split needs at least one participant.", nameof(weights));

        if (weights.Select(w => w.UserId).Distinct().Count() != weights.Count)
            throw new ArgumentException("A participant may appear in a split only once.", nameof(weights));

        if (weights.Any(w => w.Weight < 0))
            throw new ArgumentException("A weight cannot be negative.", nameof(weights));

        var totalWeight = weights.Sum(w => (long)w.Weight);

        if (totalWeight == 0)
            throw new ArgumentException("Weights must not all be zero.", nameof(weights));

        var remainderIndex = RemainderIndex(payerId, weights);

        var splits = new SplitAmount[weights.Count];
        var distributed = 0m;

        for (var i = 0; i < weights.Count; i++)
        {
            if (i == remainderIndex)
                continue;

            var share = TruncateToCents(amount * weights[i].Weight / totalWeight);

            splits[i] = new SplitAmount(weights[i].UserId, share);
            distributed += share;
        }

        // Whatever the truncations left over, rather than a share of its own, so the
        // parts sum to the whole by construction and not by luck.
        splits[remainderIndex] = new SplitAmount(weights[remainderIndex].UserId, amount - distributed);

        return splits;
    }

    /// <summary>
    /// Divides <paramref name="amount"/> evenly, which is what a category with no default
    /// rule means and what a group with no categories at all falls back to.
    /// </summary>
    public static IReadOnlyList<SplitAmount> DivideEvenly(
        decimal amount,
        Guid payerId,
        IEnumerable<Guid> participants) =>
        Divide(amount, payerId, [..participants.Select(id => new SplitWeight(id, 1))]);

    private static int RemainderIndex(Guid payerId, IReadOnlyList<SplitWeight> weights)
    {
        var payerIndex = -1;

        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i].UserId != payerId) continue;

            payerIndex = i;
            break;
        }

        // A weight of zero is a participant who owes nothing; handing them the remainder
        // would charge them the one thing they were excluded from.
        if (payerIndex >= 0 && weights[payerIndex].Weight > 0)
            return payerIndex;

        var fallback = 0;

        for (var i = 1; i < weights.Count; i++)
        {
            if (weights[i].Weight > weights[fallback].Weight ||
                (weights[i].Weight == weights[fallback].Weight &&
                 weights[i].UserId.CompareTo(weights[fallback].UserId) < 0))
            {
                fallback = i;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Toward zero, so a refund truncates the same way an expense does and the remainder
    /// stays the payer's in both directions.
    /// </summary>
    private static decimal TruncateToCents(decimal value) => Math.Truncate(value * 100m) / 100m;
}

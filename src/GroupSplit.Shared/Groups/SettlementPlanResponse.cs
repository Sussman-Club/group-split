namespace GroupSplit.Shared;

/// <summary>
/// How the caller clears everything they are on either end of, in the fewest payments --
/// across every group at once, and grouped by the person rather than by the group.
/// </summary>
/// <remarks>
/// The per-group minimised plan has been computed since <c>GET /groups/{id}/balances</c>
/// existed and was never shown anywhere. This is that same arithmetic run over every group
/// the caller is in and then added up per person, which is the axis a payment actually has:
/// somebody who owes Daniel 18.40 in one group and 40.00 in another owes Daniel 58.40 and
/// pays it once.
/// <para>
/// <see cref="Net"/>, <see cref="OwedToYou"/> and <see cref="YouOwe"/> are the same three
/// figures <see cref="UserPositionResponse"/> leads with, and for the same reason the two
/// gross sides are kept apart there: they are different people and they do not cancel out.
/// They are restated here so a client can render this screen from one read.
/// </para>
/// <para>
/// A group balance and a person balance answer different questions and only the second one
/// can be paid. Every line here has the caller on one end -- money moving between two other
/// members is the group's own business, and its Overview is where that is shown.
/// </para>
/// </remarks>
/// <param name="YouPay">
/// What the caller owes, by person, largest first. The half they can act on unilaterally,
/// which is why it is listed first on the screen.
/// </param>
/// <param name="OwedToYou">What is owed to the caller, by person, largest first.</param>
/// <param name="LastSettled">
/// When the caller last recorded a repayment in any group, or null if they never have. It
/// is what makes "did I already square up?" answerable without opening every group in turn.
/// </param>
public record SettlementPlanResponse(
    decimal Net,
    decimal OwedToYouTotal,
    decimal YouOweTotal,
    IReadOnlyList<PersonSettlement> YouPay,
    IReadOnlyList<PersonSettlement> OwedToYou,
    DateTimeOffset? LastSettled);

/// <summary>
/// One person, and the one payment that squares the caller with them.
/// </summary>
/// <param name="Amount">
/// The whole of it, across every group. Always positive: which way it goes is which list
/// the line is in.
/// </param>
/// <param name="Groups">
/// Where the figure comes from, so the arithmetic is checkable on the row rather than by
/// opening two groups. It always sums to <paramref name="Amount"/>.
/// </param>
/// <param name="IsGuest">
/// True when this person is a guest -- of the one group in <paramref name="Groups"/>, since a
/// guest belongs to exactly one. The same real person can be a guest in two groups and is
/// then two people here, each named for its group.
/// </param>
public record PersonSettlement(
    Guid UserId,
    string UserName,
    decimal Amount,
    IReadOnlyList<GroupDebt> Groups,
    bool IsGuest = false)
{
    /// <summary>
    /// Spends one payment over the groups the debt spans, largest group first.
    /// </summary>
    /// <remarks>
    /// The caller does not choose, and that is the point of the screen: which group a
    /// payment lands in is bookkeeping, and the person handing over the money should not
    /// have to do it. Largest first clears whole groups soonest, so a partial payment
    /// leaves the fewest groups half-settled.
    /// <para>
    /// Anything beyond what is outstanding -- somebody rounding up -- goes onto the largest
    /// group. It has to land somewhere, and that is where an overpayment is least
    /// surprising to find.
    /// </para>
    /// <para>
    /// Here rather than in the API so the CLI's confirmation runs the rule the API writes
    /// with, instead of a copy that has to agree with it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<GroupDebt> Allocate(decimal amount)
    {
        var allocations = new List<GroupDebt>(Groups.Count);
        var remaining = amount;

        // The plan already lists them largest first; sorted again so the rule does not
        // depend on the order a list arrived in.
        foreach (var @group in Groups.OrderByDescending(@group => @group.Amount))
        {
            if (remaining <= 0)
                break;

            var part = Math.Min(remaining, @group.Amount);
            allocations.Add(@group with { Amount = part });
            remaining -= part;
        }

        if (remaining > 0 && allocations.Count > 0)
            allocations[0] = allocations[0] with { Amount = allocations[0].Amount + remaining };

        return allocations;
    }
}

/// <summary>One group's part of what two people owe each other.</summary>
public record GroupDebt(Guid GroupId, string GroupName, decimal Amount);

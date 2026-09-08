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
public record PersonSettlement(
    Guid UserId,
    string UserName,
    decimal Amount,
    IReadOnlyList<GroupDebt> Groups);

/// <summary>One group's part of what two people owe each other.</summary>
public record GroupDebt(Guid GroupId, string GroupName, decimal Amount);

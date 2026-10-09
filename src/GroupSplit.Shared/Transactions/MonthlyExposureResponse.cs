namespace GroupSplit.Shared;

/// <summary>
/// One month of the caller's exposure: what they fronted, and what it actually cost them.
/// </summary>
/// <remarks>
/// The one series in the product worth drawing. Two figures rather than one because the
/// gap between them is the whole story -- it is how much somebody is habitually paying for
/// other people and waiting to get back. A single "spend" line would hide exactly that.
/// <para>
/// Gross, and over expenses only: settlements are transfers, so nothing here has been paid
/// back. <c>GET /users/me/position</c> remains the one answer to "where do I stand".
/// </para>
/// <para>
/// One currency for the whole series, named on every month: a line that added euros to
/// dollars would plot a number that is not an amount of anything. Expenses in any other
/// currency are left out of it, and the summary endpoints are where they are reported.
/// </para>
/// </remarks>
/// <param name="Month">The first day of the month, so a client has a date to plot rather than two ints.</param>
/// <param name="Paid">What the caller paid out that month, whoever it was for.</param>
/// <param name="Share">Their own part of every expense they were in that month, their own included.</param>
public record MonthlyExposureResponse(DateOnly Month, decimal Paid, decimal Share)
{
    /// <summary>
    /// The currency of both figures, chosen as <see cref="TransactionSummaryResponse.Currency"/>
    /// is, over the whole series rather than month by month.
    /// </summary>
    public string Currency { get; init; } = "USD";
}

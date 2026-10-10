namespace GroupSplit.App.Shared.Models;

/// <summary>
/// Which calendar day a transaction's stored instant belongs to, whether it has a time of day
/// at all, and the instant a day with no time is stored as. Every screen that shows a
/// transaction's date, and every date filter, reads the day through here.
/// </summary>
/// <remarks>
/// A transaction is stored as an instant, and two kinds of instant are in the data. Most are
/// a moment: the date and time somebody recorded, shown in each reader's own zone, with the
/// time. Rows from the bank import and the legacy workbook are a date with no time, written
/// as midnight UTC (<c>InboxService</c> does
/// <c>row.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)</c>), and so is a repayment
/// whose day was picked in a settle dialog.
/// <para>
/// So the rule (#252): an instant at exactly 00:00:00Z is a date, and it is that UTC day
/// wherever the reader is, with no time -- a charge the bank dated 9 October reads 9 October
/// in New York, not 20:00 on the 8th. Any other instant is a moment and is shown as one, in
/// the reader's zone. It is a heuristic: a moment that lands on midnight UTC to the tick is
/// read as a date. Storing the day itself (a <c>DateOnly</c>, option B on the issue) is what
/// would make it exact, and that is an API and data change this does not make.
/// </para>
/// </remarks>
public static class TransactionDay
{
    /// <summary>Whether the instant is a date written as midnight UTC, rather than a moment.</summary>
    public static bool IsDate(DateTimeOffset stored) =>
        stored.UtcTicks % TimeSpan.TicksPerDay == 0;

    /// <summary>
    /// The day the stored instant belongs to for somebody at <paramref name="offset"/>: its
    /// UTC day when it is a date, the reader's day otherwise.
    /// </summary>
    public static DateOnly Of(DateTimeOffset stored, TimeSpan offset) =>
        DateOnly.FromDateTime(IsDate(stored)
            ? stored.UtcDateTime
            : stored.ToOffset(offset).DateTime);

    /// <summary>
    /// The time of day to show beside <see cref="Of"/>, in the reader's zone; none for a date,
    /// which has no time to show.
    /// </summary>
    public static TimeOnly? TimeOf(DateTimeOffset stored, TimeSpan offset) =>
        IsDate(stored) ? null : TimeOnly.FromDateTime(stored.ToOffset(offset).DateTime);

    /// <summary>
    /// The instant a day with no time is stored as: midnight UTC of that day, which
    /// <see cref="Of"/> reads back as the same day in every zone -- how the bank import stores
    /// its rows.
    /// </summary>
    public static DateTimeOffset Stored(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <inheritdoc cref="Stored(DateOnly)"/>
    public static DateTimeOffset Stored(DateTime day) => Stored(DateOnly.FromDateTime(day));

    /// <summary>
    /// The offset, in minutes, to hand the API with a day it counts activity up to
    /// (<c>?through=&amp;utcOffset=</c>), so that its day ends where the date filter's does.
    /// </summary>
    /// <remarks>
    /// The API ends a day at midnight at the offset it is given. The date filter ends one at
    /// the earlier of the person's midnight and the UTC midnight a date is stored at (see
    /// <see cref="DateFilter.Bounds"/>): the person's own offset east of Greenwich, and zero
    /// west of it. Sending that instead of the raw offset keeps a bank row posted on the 1st
    /// out of a settlement "through the 31st" in New York, and keeps the expenses a
    /// settlement's period lists the same ones the API says it covered.
    /// </remarks>
    public static int CutoffOffsetMinutes(TimeSpan offset) =>
        (int)Math.Max(offset.TotalMinutes, 0);
}

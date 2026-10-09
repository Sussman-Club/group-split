namespace GroupSplit.App.Shared.Models;

/// <summary>
/// Which calendar day a transaction's stored instant belongs to, and the instant a day
/// somebody picked is stored as. Every screen that shows a transaction's date, and every
/// date filter, reads the day through here.
/// </summary>
/// <remarks>
/// A transaction happens on a day, not at a moment, but the API stores an instant. Two kinds
/// of instant are in the data. Rows from the bank import and the legacy workbook are a date
/// written as midnight UTC (<c>InboxService</c> does
/// <c>row.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)</c>), and so is every
/// transaction a day was picked for in the web app. Everything else is the clock reading the
/// create call landed on.
/// <para>
/// So the rule, decided in #252: an instant at exactly 00:00:00Z is a date, and it is that
/// UTC day wherever the reader is -- a charge the bank dated 12 September reads 12 September
/// in New York, not the evening of the 11th. Any other instant is a moment somebody recorded
/// it at, and it is the day that moment fell on where the reader is. It is a heuristic: a
/// moment that happens to land on midnight UTC to the tick is read as a date. Storing the
/// day itself (a <c>DateOnly</c>, option B on the issue) is what would make it exact, and
/// that is an API and data change this does not make.
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
    /// The instant a picked day is stored as: midnight UTC of that day, which <see cref="Of"/>
    /// reads back as the same day in every zone.
    /// </summary>
    /// <remarks>
    /// Not midnight where the person is, which is a different instant in every zone and the
    /// previous evening in UTC for anybody east of Greenwich; and not the moment of entry,
    /// whose day depends on who reads it. A day picked as "today" is therefore stored as
    /// today whatever the hour it was typed in.
    /// </remarks>
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

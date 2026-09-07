namespace GroupSplit.App.Shared.Models;

/// <summary>The named spans a listing offers, plus the one someone picks themselves.</summary>
public enum DateFilterPreset
{
    AllTime,
    ThisMonth,
    LastMonth,
    LastThreeMonths,
    ThisYear,
    Custom
}

/// <summary>The two instants a span resolves to. Either may be null for "no bound that side".</summary>
public readonly record struct DateBounds(DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// The two calendar days a span resolves to, both ends included. Either may be null for "no
/// bound that side".
/// </summary>
public readonly record struct DayBounds(DateOnly? From, DateOnly? To);

/// <summary>
/// A span of days to narrow a listing to, as the person chose it: a preset, or two dates
/// of their own. Kept as the choice rather than as the two dates it resolves to, so the
/// chips can show which one is on, and so "this month" still means this month tomorrow.
/// </summary>
/// <remarks>
/// The bounds are turned into instants only when a request is about to be made, and only
/// against an offset and a "today" the caller supplies -- see <see cref="Bounds"/>. A day
/// is a local idea: someone in Lisbon asking for March means March where they are, not
/// March UTC. This used to read <c>TimeZoneInfo.Local</c> and <c>DateTime.Today</c> itself,
/// which under WebAssembly is not the browser's zone; the offset now comes from
/// <c>LocalClock</c>, which asks the browser, and this type has no clock of its own.
/// </remarks>
public sealed record DateFilter(DateFilterPreset Preset = DateFilterPreset.AllTime,
    DateTime? CustomFrom = null,
    DateTime? CustomTo = null)
{
    public static readonly DateFilter AllTime = new();

    public bool IsAllTime => Preset is DateFilterPreset.AllTime
        || (Preset is DateFilterPreset.Custom && CustomFrom is null && CustomTo is null);

    public string Label => Preset switch
    {
        DateFilterPreset.ThisMonth => "This month",
        DateFilterPreset.LastMonth => "Last month",
        DateFilterPreset.LastThreeMonths => "Last 3 months",
        DateFilterPreset.ThisYear => "This year",
        DateFilterPreset.Custom => Describe(),
        _ => "All time"
    };

    /// <summary>
    /// The span as two instants, sent as UTC. <paramref name="offset"/> is the person's zone
    /// and <paramref name="today"/> their calendar day, both from <c>LocalClock</c>: the
    /// first instant is midnight at the start of the first day where they are, the last is
    /// the very end of the last day, since the filter takes both of its ends and a bound of
    /// midnight would drop that day.
    /// </summary>
    public DateBounds Bounds(TimeSpan offset, DateTime today)
    {
        var (from, to) = Span(today);

        return new DateBounds(
            from is { } start ? StartOfDay(start, offset) : null,
            to is { } end ? StartOfDay(end.AddDays(1), offset).AddTicks(-1) : null);
    }

    /// <summary>
    /// The span as the first and last calendar day it covers, both included.
    /// </summary>
    /// <remarks>
    /// For a listing whose rows are dated by a day rather than by an instant. An imported
    /// bank row is one: the date on a statement is a calendar date the bank decided on, not
    /// a moment, so it takes no offset and turning it into one would only invent a time to
    /// then have to argue about. <paramref name="today"/> is still the person's day, from
    /// <c>LocalClock</c>, because which month "this month" is remains a local question.
    /// </remarks>
    public DayBounds Days(DateTime today)
    {
        var (from, to) = Span(today);

        return new DayBounds(
            from is { } start ? DateOnly.FromDateTime(start) : null,
            to is { } end ? DateOnly.FromDateTime(end) : null);
    }

    /// <summary>
    /// The preset as the two local days it runs between. Both readings above go through
    /// here, so a preset cannot come to mean one span as instants and a different one as
    /// days.
    /// </summary>
    private (DateTime? From, DateTime? To) Span(DateTime today)
    {
        var firstOfMonth = new DateTime(today.Year, today.Month, 1);

        DateTime? from = Preset switch
        {
            DateFilterPreset.ThisMonth => firstOfMonth,
            DateFilterPreset.LastMonth => firstOfMonth.AddMonths(-1),
            // Two months back plus this one, so "last 3 months" includes the month in progress.
            DateFilterPreset.LastThreeMonths => firstOfMonth.AddMonths(-2),
            DateFilterPreset.ThisYear => new DateTime(today.Year, 1, 1),
            DateFilterPreset.Custom => CustomFrom,
            _ => null
        };

        DateTime? to = Preset switch
        {
            DateFilterPreset.ThisMonth => firstOfMonth.AddMonths(1).AddDays(-1),
            DateFilterPreset.LastMonth => firstOfMonth.AddDays(-1),
            DateFilterPreset.LastThreeMonths => firstOfMonth.AddMonths(1).AddDays(-1),
            DateFilterPreset.ThisYear => new DateTime(today.Year, 12, 31),
            DateFilterPreset.Custom => CustomTo,
            _ => null
        };

        return (from, to);
    }

    private static DateTimeOffset StartOfDay(DateTime day, TimeSpan offset) =>
        new DateTimeOffset(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), offset).ToUniversalTime();

    private string Describe() => (CustomFrom, CustomTo) switch
    {
        (null, null) => "All time",
        ({ } from, null) => $"From {from:d MMM yyyy}",
        (null, { } to) => $"Until {to:d MMM yyyy}",
        ({ } from, { } to) when from.Date == to.Date => $"{from:d MMM yyyy}",
        var (from, to) => $"{from:d MMM} – {to:d MMM yyyy}"
    };
}

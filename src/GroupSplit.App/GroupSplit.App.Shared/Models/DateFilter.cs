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

/// <summary>
/// A span of days to narrow a listing to, as the person chose it: a preset, or two dates
/// of their own. Kept as the choice rather than as the two dates it resolves to, so the
/// chips can show which one is on, and so "this month" still means this month tomorrow.
/// </summary>
/// <remarks>
/// The bounds are turned into instants only when a request is about to be made, at the
/// local offset, because a day is a local idea: someone in Lisbon asking for March means
/// March where they are, not March UTC.
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
    /// The first instant included, or null for no lower bound. Midnight at the start of the
    /// day, at the local offset.
    /// </summary>
    public DateTimeOffset? From => StartOfDay(Preset switch
    {
        DateFilterPreset.ThisMonth => FirstOfThisMonth,
        DateFilterPreset.LastMonth => FirstOfThisMonth.AddMonths(-1),
        // Two months back plus this one, so "last 3 months" includes the month in progress.
        DateFilterPreset.LastThreeMonths => FirstOfThisMonth.AddMonths(-2),
        DateFilterPreset.ThisYear => new DateTime(DateTime.Today.Year, 1, 1),
        DateFilterPreset.Custom => CustomFrom,
        _ => null
    });

    /// <summary>
    /// The last instant included, or null for no upper bound. The very end of the day, since
    /// the filter takes both of its ends and a bound of midnight would drop that day.
    /// </summary>
    public DateTimeOffset? To => EndOfDay(Preset switch
    {
        DateFilterPreset.ThisMonth => FirstOfThisMonth.AddMonths(1).AddDays(-1),
        DateFilterPreset.LastMonth => FirstOfThisMonth.AddDays(-1),
        DateFilterPreset.LastThreeMonths => FirstOfThisMonth.AddMonths(1).AddDays(-1),
        DateFilterPreset.ThisYear => new DateTime(DateTime.Today.Year, 12, 31),
        DateFilterPreset.Custom => CustomTo,
        _ => null
    });

    private static DateTime FirstOfThisMonth => new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private static DateTimeOffset? StartOfDay(DateTime? day) =>
        day is { } value ? AtLocalOffset(value.Date) : null;

    private static DateTimeOffset? EndOfDay(DateTime? day) =>
        day is { } value ? AtLocalOffset(value.Date.AddDays(1)).AddTicks(-1) : null;

    private static DateTimeOffset AtLocalOffset(DateTime value) =>
        new(value, TimeZoneInfo.Local.GetUtcOffset(value));

    private string Describe() => (CustomFrom, CustomTo) switch
    {
        (null, null) => "All time",
        ({ } from, null) => $"From {from:d MMM yyyy}",
        (null, { } to) => $"Until {to:d MMM yyyy}",
        ({ } from, { } to) when from.Date == to.Date => $"{from:d MMM yyyy}",
        var (from, to) => $"{from:d MMM} – {to:d MMM yyyy}"
    };
}

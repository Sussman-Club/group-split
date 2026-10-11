using System.Globalization;

namespace GroupSplit.App.Shared.Models;

/// <summary>
/// How a listing's narrowing is written into the query string and read back out of it, so a
/// refresh, the back button or a shared link lands on the view the reader had (#398).
/// </summary>
/// <remarks>
/// Whatever the page opens on is left out, so an untouched page keeps a bare address. A value
/// that does not parse -- a date nobody can read, a page of "abc" -- reads as that default
/// rather than as an error: a mangled link should still open the page.
/// </remarks>
public static class ListingQuery
{
    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>The page size a grid opens on, and the others its pager offers.</summary>
    public const int DefaultPageSize = 10;

    private static readonly int[] PageSizes = [10, 25, 50];

    /// <summary>The sort a ledger opens on: newest first.</summary>
    public static readonly (string Key, bool Descending) DefaultSort = ("dateTime", true);

    private static readonly (string Name, DateFilterPreset Preset)[] Presets =
    [
        ("all", DateFilterPreset.AllTime),
        ("this-month", DateFilterPreset.ThisMonth),
        ("last-month", DateFilterPreset.LastMonth),
        ("last-3-months", DateFilterPreset.LastThreeMonths),
        ("this-year", DateFilterPreset.ThisYear)
    ];

    /// <summary>
    /// The span as query values: a preset by name, so "this month" stays this month, and a
    /// custom range as its two days, so a shared link shows the same rows.
    /// </summary>
    public static (string? Range, string? From, string? To) FormatRange(DateFilter range, DateFilter opensOn)
    {
        if (range == opensOn)
            return (null, null, null);

        if (range.IsAllTime)
            return ("all", null, null);

        if (range.Preset is DateFilterPreset.Custom)
            return (null, range.CustomFrom?.ToString(DayFormat, CultureInfo.InvariantCulture),
                range.CustomTo?.ToString(DayFormat, CultureInfo.InvariantCulture));

        return (Presets.First(preset => preset.Preset == range.Preset).Name, null, null);
    }

    public static DateFilter ParseRange(string? range, string? from, string? to, DateFilter opensOn)
    {
        var start = ParseDay(from);
        var end = ParseDay(to);

        if (start is not null || end is not null)
            return start > end ? opensOn : new DateFilter(DateFilterPreset.Custom, start, end);

        foreach (var (name, preset) in Presets)
        {
            if (string.Equals(name, range, StringComparison.OrdinalIgnoreCase))
                return new DateFilter(preset);
        }

        return opensOn;
    }

    private static DateTime? ParseDay(string? value) =>
        DateTime.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    /// <summary>The grid's zero-based page as the one-based number a reader would write.</summary>
    public static string? FormatPage(int page) =>
        page > 0 ? (page + 1).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>The zero-based page a <c>page</c> value asks for.</summary>
    public static int ParsePage(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 1
            ? page - 1
            : 0;

    public static string? FormatPageSize(int size) =>
        size == DefaultPageSize ? null : size.ToString(CultureInfo.InvariantCulture);

    /// <summary>A page size the pager offers, or the default for anything else.</summary>
    public static int ParsePageSize(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && PageSizes.Contains(size)
            ? size
            : DefaultPageSize;

    /// <summary>The sort as one value: the key, with a leading minus when it runs descending.</summary>
    public static string? FormatSort((string Key, bool Descending)? sort) =>
        sort is not { } given || given == DefaultSort
            ? null
            : (given.Descending ? "-" : "") + given.Key;

    /// <summary>
    /// The sort a <c>sort</c> value asks for, when it names one of <paramref name="keys"/>;
    /// otherwise the default.
    /// </summary>
    public static (string Key, bool Descending) ParseSort(string? value, params string[] keys)
    {
        var descending = value?.StartsWith('-') == true;
        var key = descending ? value![1..] : value;

        return keys.FirstOrDefault(known => string.Equals(known, key, StringComparison.OrdinalIgnoreCase)) is { } match
            ? (match, descending)
            : DefaultSort;
    }
}

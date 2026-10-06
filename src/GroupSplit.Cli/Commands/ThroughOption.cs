using System.CommandLine;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// <c>--through &lt;day&gt;</c>: settle, or show what is owed for, activity dated up to a day.
/// </summary>
/// <remarks>
/// A day rather than an instant, like the inbox's <c>--from</c> and <c>--to</c>: "through 31
/// August" is a date on a calendar. The API needs to know when that day ends, so every request
/// carrying one also carries this machine's offset from UTC <em>at the end of that day</em>,
/// which keeps a cutoff on the far side of a daylight-saving change exact.
/// <para>
/// What it counts is the API's rule, not the CLI's: expenses dated up to the day, and every
/// settlement whatever its date -- so paying August in October never asks again for what was
/// already paid in September.
/// </para>
/// </remarks>
internal static class ThroughOption
{
    public static Option<DateOnly?> Create(string description =
        "Only activity dated up to this day, e.g. 2026-08-31. Payments count whatever their date.") =>
        new("--through") { Description = description };

    /// <summary>This machine's offset from UTC, in minutes, at the moment the day ends.</summary>
    public static int? OffsetFor(DateOnly? through) =>
        through is { } day && day < DateOnly.MaxValue
            ? (int)TimeZoneInfo.Local.GetUtcOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue)).TotalMinutes
            : null;

    /// <summary><c> --through 2026-08-31</c>, or nothing, for a command line that has to say it again.</summary>
    public static string Flag(DateOnly? through) =>
        through is { } day ? $" --through {day:yyyy-MM-dd}" : string.Empty;
}

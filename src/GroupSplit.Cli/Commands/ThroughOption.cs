using System.CommandLine;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// <c>--through &lt;day&gt;</c>: settle, or show what is owed for, activity dated up to a day.
/// </summary>
/// <remarks>
/// A day rather than an instant, like the inbox's <c>--from</c> and <c>--to</c>: "through 31
/// August" is a date on a calendar. The API needs to know when that day ends, so every request
/// carrying one also carries this machine's offset from UTC as it is now -- the same offset
/// the web app sends, so the two agree about which side of the line an expense falls on.
/// <para>
/// What it counts is the API's rule, not the CLI's: expenses dated up to the day, the
/// settlements made for it, and later ones only as far as they pay down what it left -- so
/// paying August in October never asks again for what was already paid in September.
/// </para>
/// </remarks>
internal static class ThroughOption
{
    public static Option<DateOnly?> Create(string description =
        "Only activity dated up to this day, e.g. 2026-08-31. Later payments count only as far as they pay it down.") =>
        new("--through") { Description = description };

    /// <summary>This machine's offset from UTC, in minutes, when a day was given.</summary>
    public static int? OffsetFor(DateOnly? through) =>
        through is null ? null : LocalOffsetMinutes;

    /// <summary>This machine's offset from UTC now, in minutes.</summary>
    public static int LocalOffsetMinutes =>
        (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
}

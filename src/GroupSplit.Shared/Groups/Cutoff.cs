using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// The last day of activity a balance counts, on the calendar of whoever is asking.
/// </summary>
/// <remarks>
/// What settling up "through 31 August" means. A balance through a day counts every
/// expense dated on or before it and <em>every settlement, whatever its date</em> -- money
/// paid goes against the oldest debt first. Cutting settlements off at the day as well
/// would ask somebody who paid in September to pay August again when August is settled in
/// October, because their payment falls after the line.
/// <para>
/// A day and an offset rather than an instant, because a day ends at a different moment
/// for everybody: the offset is the caller's, the same one the date filters already turn a
/// day into instants with, so "through 31 August" here means what "to 31 August" means on
/// the expense listing beside it. Transaction dates are still stored as instants (#252);
/// until they are days, a bank row stored at midnight UTC can land on the other side of a
/// cutoff from the day the bank gave it.
/// </para>
/// </remarks>
/// <param name="Through">The last day counted, inclusive.</param>
/// <param name="UtcOffsetMinutes">
/// The caller's offset from UTC in minutes -- <c>-240</c> for New York in summer. Zero when
/// the caller does not say.
/// </param>
public record Cutoff(DateOnly Through, int UtcOffsetMinutes = 0)
{
    /// <summary>The widest offset any time zone uses, either way.</summary>
    public const int MaxOffsetMinutes = 14 * 60;

    /// <summary>
    /// The first instant after the day, in UTC: an expense counts when it is dated before
    /// this. Exclusive, so nothing at the boundary is counted twice or not at all.
    /// </summary>
    public DateTimeOffset End =>
        Through == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(Through.AddDays(1).ToDateTime(TimeOnly.MinValue),
                TimeSpan.FromMinutes(UtcOffsetMinutes)).ToUniversalTime();

    /// <summary>
    /// The cutoff a request describes, or null when it names no day -- which is everything,
    /// exactly as before there was a cutoff to give.
    /// </summary>
    public static Cutoff? Of(DateOnly? through, int? utcOffsetMinutes) =>
        through is { } day ? new Cutoff(day, utcOffsetMinutes ?? 0) : null;
}

/// <summary>
/// A cutoff as query parameters: <c>?through=2026-08-31&amp;utcOffset=-240</c>.
/// </summary>
public record CutoffQuery
{
    /// <summary>The last day of activity to count, or nothing to count everything.</summary>
    public DateOnly? Through { get; init; }

    /// <summary>The caller's offset from UTC in minutes, which says when that day ends.</summary>
    [Range(-Cutoff.MaxOffsetMinutes, Cutoff.MaxOffsetMinutes,
        ErrorMessage = "The offset from UTC must be within 14 hours either way.")]
    public int? UtcOffset { get; init; }

    public Cutoff? ToCutoff() => Cutoff.Of(Through, UtcOffset);
}

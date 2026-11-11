using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// The last day of activity a balance counts, on the calendar of whoever is asking.
/// </summary>
/// <remarks>
/// What settling up "through 31 August" means. A balance through a day counts every
/// expense dated on or before it, and the settlements made for it: those covering through
/// it or an earlier day, and those claiming no period dated on or before it. Settlements
/// made for after it count only as far as they pay down what the period left -- money paid
/// goes against the oldest debt first, so somebody who paid in September is not asked for
/// August again when August is settled in October, and a payment made for a later period
/// never reads as being owed back for this one. Nothing else dated after the day moves the
/// figure -- being ahead in October does not shrink what August left -- until somebody
/// settles a later period: settling September writes the plan through September, which may
/// route August's debts through somebody else, so what it leaves is the most August can
/// still ask for.
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
        ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.UtcOffset))]
    public int? UtcOffset { get; init; }

    public Cutoff? ToCutoff() => Cutoff.Of(Through, UtcOffset);
}

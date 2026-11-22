using GroupSplit.Shared;

namespace GroupSplit.API.Services.RecurringExpenses;

/// <summary>
/// The days a repeating expense falls on.
/// </summary>
/// <remarks>
/// Each occurrence is counted from the first rather than from the one before it, so a monthly
/// expense started on 31 January falls on 28 (or 29) February and on 31 March again, where
/// stepping a month at a time would have stayed on the 28th for good.
/// <see cref="DateOnly.AddMonths"/> and <see cref="DateOnly.AddYears"/> already land a day
/// that does not exist on the last day of the month.
/// </remarks>
internal static class Recurrence
{
    public static DateOnly Occurrence(DateOnly startsOn, RepeatFrequency frequency, int index) => frequency switch
    {
        RepeatFrequency.Weekly => startsOn.AddDays(7 * index),
        RepeatFrequency.Monthly => startsOn.AddMonths(index),
        RepeatFrequency.Yearly => startsOn.AddYears(index),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null)
    };

    /// <summary>The first occurrence on <paramref name="day"/> or later.</summary>
    public static DateOnly OnOrAfter(DateOnly startsOn, RepeatFrequency frequency, DateOnly day)
    {
        if (day <= startsOn)
        {
            return startsOn;
        }

        // A guess that is never past the answer, then a step or two forward to it.
        var index = Math.Max(0, frequency switch
        {
            RepeatFrequency.Weekly => (day.DayNumber - startsOn.DayNumber) / 7,
            RepeatFrequency.Monthly => (day.Year - startsOn.Year) * 12 + day.Month - startsOn.Month,
            _ => day.Year - startsOn.Year
        } - 1);

        while (Occurrence(startsOn, frequency, index) < day)
        {
            index++;
        }

        return Occurrence(startsOn, frequency, index);
    }

    /// <summary>The first occurrence after <paramref name="day"/>.</summary>
    public static DateOnly After(DateOnly startsOn, RepeatFrequency frequency, DateOnly day) =>
        OnOrAfter(startsOn, frequency, day.AddDays(1));
}

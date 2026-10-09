using GroupSplit.App.Shared.Models;

namespace GroupSplit.App.Web.Test.Time;

/// <summary>
/// Which day a stored instant belongs to, and the span a month filter turns into (#252).
/// </summary>
/// <remarks>
/// Checked for two readers on either side of Greenwich: New York at UTC-5, where midnight UTC
/// is the previous evening, and Sydney at UTC+10, where it is the next morning. A bank row is
/// a date stored as midnight UTC, and it has to read as the bank's date for both of them, and
/// fall in the bank's month in both of their filters.
/// </remarks>
public class TransactionDayTest
{
    private static readonly TimeSpan NewYork = TimeSpan.FromHours(-5);
    private static readonly TimeSpan Sydney = TimeSpan.FromHours(10);

    /// <summary>How an imported bank row is stored: its posting date at midnight UTC.</summary>
    private static DateTimeOffset BankRow(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> Offsets => [-5, 10];

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_bank_row_reads_as_the_banks_date_in_any_zone(int hours)
    {
        var posted = BankRow(2026, 9, 12);

        Assert.True(TransactionDay.IsDate(posted));
        Assert.Equal(new DateOnly(2026, 9, 12), TransactionDay.Of(posted, TimeSpan.FromHours(hours)));
    }

    [Fact]
    public void A_moment_reads_as_the_day_it_fell_on_where_the_reader_is()
    {
        // 23:30 in New York on the 11th is 04:30 UTC on the 12th.
        var recorded = new DateTimeOffset(2026, 9, 11, 23, 30, 0, NewYork);

        Assert.False(TransactionDay.IsDate(recorded));
        Assert.Equal(new DateOnly(2026, 9, 11), TransactionDay.Of(recorded, NewYork));
        Assert.Equal(new DateOnly(2026, 9, 12), TransactionDay.Of(recorded, Sydney));
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_picked_day_is_stored_as_a_date_that_reads_back_as_that_day(int hours)
    {
        var stored = TransactionDay.Stored(new DateOnly(2026, 9, 30));

        Assert.Equal(TimeSpan.Zero, stored.Offset);
        Assert.True(TransactionDay.IsDate(stored));
        Assert.Equal(new DateOnly(2026, 9, 30), TransactionDay.Of(stored, TimeSpan.FromHours(hours)));
    }

    /// <summary>
    /// The case from the issue: a month filter keeps a row posted on the 1st, and keeps out
    /// one posted on the 1st of the next month, whichever side of Greenwich the reader is.
    /// </summary>
    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_month_holds_a_row_posted_on_its_first_and_not_one_posted_on_the_next_first(int hours)
    {
        var offset = TimeSpan.FromHours(hours);
        var bounds = new DateFilter(DateFilterPreset.ThisMonth).Bounds(offset, new DateTime(2026, 9, 15));

        bool Within(DateTimeOffset stored) => stored >= bounds.From && stored <= bounds.To;

        Assert.True(Within(BankRow(2026, 9, 1)), "a row posted on 1 September is in September");
        Assert.True(Within(BankRow(2026, 9, 30)), "a row posted on 30 September is in September");
        Assert.False(Within(BankRow(2026, 10, 1)), "a row posted on 1 October is not in September");
        Assert.False(Within(BankRow(2026, 8, 31)), "a row posted on 31 August is not in September");
    }

    [Fact]
    public void The_api_cutoff_offset_is_the_readers_east_of_greenwich_and_zero_west_of_it()
    {
        Assert.Equal(600, TransactionDay.CutoffOffsetMinutes(Sydney));
        Assert.Equal(0, TransactionDay.CutoffOffsetMinutes(NewYork));
    }
}

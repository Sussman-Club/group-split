using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Time;

/// <summary>
/// Which day a stored instant belongs to, whether it has a time, what an edit stores, and
/// the span a month filter turns into (#252).
/// </summary>
/// <remarks>
/// Checked for two readers on either side of Greenwich: New York at UTC-5, where midnight UTC
/// is the previous evening, and Sydney at UTC+10, where it is the next morning. A bank row is
/// a date stored as midnight UTC, and it has to read as the bank's date, with no time, for
/// both of them, and fall in the bank's month in both of their filters. Anything else is a
/// moment, and keeps its time in the reader's zone.
/// </remarks>
public class TransactionDayTest
{
    private static readonly TimeSpan NewYork = TimeSpan.FromHours(-5);
    private static readonly TimeSpan Sydney = TimeSpan.FromHours(10);

    /// <summary>How an imported bank row is stored: its posting date at midnight UTC.</summary>
    private static DateTimeOffset BankRow(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> Offsets => [-5, 10];

    /// <summary>The clock of somebody <paramref name="hours"/> from UTC, as WebAssembly builds it.</summary>
    private static LocalClock ReaderAt(int hours)
    {
        var js = new Mock<IJSInProcessRuntime>();

        js.Setup(runtime => runtime.Invoke<int>("gs.tzOffsetMinutes", It.IsAny<object?[]?>()))
            .Returns(hours * 60);

        return new LocalClock(js.Object);
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_bank_row_reads_as_the_banks_date_and_has_no_time_in_any_zone(int hours)
    {
        var posted = BankRow(2026, 10, 9);

        Assert.True(TransactionDay.IsDate(posted));
        Assert.Equal(new DateOnly(2026, 10, 9), TransactionDay.Of(posted, TimeSpan.FromHours(hours)));
        Assert.Null(TransactionDay.TimeOf(posted, TimeSpan.FromHours(hours)));
    }

    [Fact]
    public void A_moment_reads_as_the_day_and_time_it_fell_on_where_the_reader_is()
    {
        // 23:30 in New York on the 11th is 04:30 UTC on the 12th, and 14:30 in Sydney.
        var recorded = new DateTimeOffset(2026, 9, 11, 23, 30, 0, NewYork);

        Assert.False(TransactionDay.IsDate(recorded));
        Assert.Equal(new DateOnly(2026, 9, 11), TransactionDay.Of(recorded, NewYork));
        Assert.Equal(new TimeOnly(23, 30), TransactionDay.TimeOf(recorded, NewYork));
        Assert.Equal(new DateOnly(2026, 9, 12), TransactionDay.Of(recorded, Sydney));
        Assert.Equal(new TimeOnly(14, 30), TransactionDay.TimeOf(recorded, Sydney));
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_day_with_no_time_is_stored_as_a_date_that_reads_back_as_that_day(int hours)
    {
        var stored = TransactionDay.Stored(new DateOnly(2026, 9, 30));

        Assert.Equal(TimeSpan.Zero, stored.Offset);
        Assert.True(TransactionDay.IsDate(stored));
        Assert.Equal(new DateOnly(2026, 9, 30), TransactionDay.Of(stored, TimeSpan.FromHours(hours)));
    }

    /// <summary>
    /// The case from the issue: a month filter keeps a row posted on the 1st, and keeps out
    /// one posted on the 1st of the next month, whichever side of Greenwich the reader is.
    /// With local-midnight bounds New York lost the first and gained the second.
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

    // ---- editing ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_bank_row_opens_on_its_bank_day_with_an_empty_time(int hours)
    {
        var (date, time) = ReaderAt(hours).Pickers(BankRow(2026, 10, 9));

        Assert.Equal(new DateTime(2026, 10, 9), date);
        Assert.Null(time);
    }

    [Fact]
    public void A_moment_opens_on_its_local_day_and_time()
    {
        // 18:30 UTC on the 8th: 13:30 that day in New York.
        var (date, time) = ReaderAt(-5).Pickers(new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTime(2026, 10, 8), date);
        Assert.Equal(new TimeSpan(13, 30, 0), time);
    }

    /// <summary>
    /// Saving an edit to something else -- the name, the amount -- leaves a bank row's date
    /// exactly as it was stored. Made into a moment through the pickers, it would be local
    /// midnight: 05:00 UTC in New York, and the bank's date would gain a time nobody picked.
    /// </summary>
    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_bank_row_left_on_its_day_with_no_time_keeps_its_stored_instant(int hours)
    {
        var clock = ReaderAt(hours);
        var posted = BankRow(2026, 10, 9);
        var (date, time) = clock.Pickers(posted);

        Assert.Equal(posted, clock.Edited(posted, date, time));
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_bank_row_moved_to_another_day_stays_a_date(int hours)
    {
        var edited = ReaderAt(hours).Edited(BankRow(2026, 10, 9), new DateTime(2026, 10, 7), null);

        Assert.Equal(BankRow(2026, 10, 7), edited);
    }

    [Fact]
    public void A_time_given_to_a_bank_row_makes_it_a_moment_where_the_person_is()
    {
        var clock = ReaderAt(-5);

        var edited = clock.Edited(BankRow(2026, 10, 9), new DateTime(2026, 10, 9), new TimeSpan(14, 15, 0));

        Assert.Equal(clock.At(new DateTime(2026, 10, 9), new TimeSpan(14, 15, 0)), edited);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 19, 15, 0, TimeSpan.Zero), edited);
        Assert.False(TransactionDay.IsDate(edited));
    }

    [Fact]
    public void A_moment_is_made_from_the_pickers_as_it_always_was()
    {
        var clock = ReaderAt(10);
        var recorded = new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.Zero);

        var edited = clock.Edited(recorded, new DateTime(2026, 10, 10), new TimeSpan(9, 0, 0));

        Assert.Equal(clock.At(new DateTime(2026, 10, 10), new TimeSpan(9, 0, 0)), edited);
    }
}

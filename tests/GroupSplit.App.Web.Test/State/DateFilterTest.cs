using GroupSplit.App.Shared.Models;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The span a listing is narrowed to. A day is a local idea -- someone asking for March
/// means March where they are -- so these check the bounds land on the right instants at
/// the person's offset, and that both ends of the day are included: the filter takes both of
/// its ends, so an upper bound at midnight would drop that whole day's expenses.
/// </summary>
/// <remarks>
/// Pinned to one zone and one day rather than the machine's. The filter used to read
/// <c>TimeZoneInfo.Local</c> and <c>DateTime.Today</c> for itself, so these tests depended
/// on where and when they ran; it now takes both from the caller, and so can these. Lisbon
/// in March, when it is an hour ahead of UTC, is chosen because it makes the UTC conversion
/// visible: midnight there is 23:00 the day before in UTC.
/// </remarks>
public class DateFilterTest
{
    private static readonly TimeSpan Lisbon = TimeSpan.FromHours(1);
    private static readonly DateTime Today = new(2026, 3, 15);

    private static DateTimeOffset At(DateTime local) =>
        new DateTimeOffset(local, Lisbon).ToUniversalTime();

    private static DateBounds Bounds(DateFilter filter) => filter.Bounds(Lisbon, Today);

    [Fact]
    public void All_time_has_no_bounds_at_all()
    {
        var filter = DateFilter.AllTime;

        Assert.True(filter.IsAllTime);
        Assert.Null(Bounds(filter).From);
        Assert.Null(Bounds(filter).To);
    }

    [Fact]
    public void This_month_runs_from_the_first_to_the_end_of_the_last_day()
    {
        var bounds = Bounds(new DateFilter(DateFilterPreset.ThisMonth));

        Assert.Equal(At(new DateTime(2026, 3, 1)), bounds.From);
        Assert.Equal(At(new DateTime(2026, 4, 1)).AddTicks(-1), bounds.To);
    }

    /// <summary>The whole point of the offset: a local day starts before its UTC one.</summary>
    [Fact]
    public void Bounds_are_sent_as_utc_instants_of_the_persons_midnight()
    {
        var bounds = Bounds(new DateFilter(DateFilterPreset.ThisMonth));

        Assert.Equal(TimeSpan.Zero, bounds.From!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 23, 0, 0, TimeSpan.Zero), bounds.From);
    }

    [Fact]
    public void Last_month_ends_where_this_month_begins()
    {
        var lastMonth = Bounds(new DateFilter(DateFilterPreset.LastMonth));
        var thisMonth = Bounds(new DateFilter(DateFilterPreset.ThisMonth));

        Assert.Equal(At(new DateTime(2026, 2, 1)), lastMonth.From);

        // The two abut without overlapping: the older one ends a tick before the newer starts.
        Assert.Equal(thisMonth.From!.Value.AddTicks(-1), lastMonth.To);
    }

    /// <summary>Three months means two behind plus the one in progress, not three behind.</summary>
    [Fact]
    public void Last_three_months_includes_the_month_in_progress()
    {
        var bounds = Bounds(new DateFilter(DateFilterPreset.LastThreeMonths));

        Assert.Equal(At(new DateTime(2026, 1, 1)), bounds.From);
        Assert.Equal(At(new DateTime(2026, 4, 1)).AddTicks(-1), bounds.To);
        Assert.True(bounds.To > At(Today));
    }

    [Fact]
    public void This_year_is_january_the_first_to_the_end_of_december()
    {
        var bounds = Bounds(new DateFilter(DateFilterPreset.ThisYear));

        Assert.Equal(At(new DateTime(2026, 1, 1)), bounds.From);
        Assert.Equal(At(new DateTime(2027, 1, 1)).AddTicks(-1), bounds.To);
    }

    /// <summary>
    /// The one that would bite: an upper bound at midnight excludes everything recorded on
    /// the day the person picked, which is the day they most likely meant.
    /// </summary>
    [Fact]
    public void A_custom_range_includes_everything_on_its_last_day()
    {
        var day = new DateTime(2026, 3, 15);
        var bounds = Bounds(new DateFilter(DateFilterPreset.Custom, day, day));

        var lateThatEvening = At(day.AddHours(23).AddMinutes(59));

        Assert.True(bounds.From <= lateThatEvening);
        Assert.True(bounds.To >= lateThatEvening);
        Assert.Equal(At(day.AddDays(1)).AddTicks(-1), bounds.To);
    }

    [Fact]
    public void A_custom_range_may_be_open_at_either_end()
    {
        var day = new DateTime(2026, 3, 15);

        var since = Bounds(new DateFilter(DateFilterPreset.Custom, day, null));
        var until = Bounds(new DateFilter(DateFilterPreset.Custom, null, day));

        Assert.NotNull(since.From);
        Assert.Null(since.To);
        Assert.Null(until.From);
        Assert.NotNull(until.To);
    }

    /// <summary>
    /// Clearing the picker is not a custom range of nothing; it is no longer narrowing.
    /// Anything reading IsAllTime -- the tiles, and whether a summary is worth a request --
    /// has to see it that way.
    /// </summary>
    [Fact]
    public void A_custom_range_with_neither_end_is_all_time()
    {
        var filter = new DateFilter(DateFilterPreset.Custom);

        Assert.True(filter.IsAllTime);
        Assert.Null(Bounds(filter).From);
        Assert.Null(Bounds(filter).To);
    }

    [Theory]
    [InlineData(DateFilterPreset.AllTime, "All time")]
    [InlineData(DateFilterPreset.ThisMonth, "This month")]
    [InlineData(DateFilterPreset.LastMonth, "Last month")]
    [InlineData(DateFilterPreset.LastThreeMonths, "Last 3 months")]
    [InlineData(DateFilterPreset.ThisYear, "This year")]
    public void Each_preset_says_what_it_is(DateFilterPreset preset, string expected)
    {
        Assert.Equal(expected, new DateFilter(preset).Label);
    }

    [Fact]
    public void A_custom_range_says_its_dates()
    {
        var filter = new DateFilter(DateFilterPreset.Custom,
            new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        Assert.Contains("Mar", filter.Label);
    }

    /// <summary>
    /// The reason the choice is kept rather than the dates it works out to: two filters
    /// made the same way are the same value, which is what lets the page state tell "the
    /// span I am already showing" from a new question.
    /// </summary>
    [Fact]
    public void Two_filters_made_the_same_way_are_equal()
    {
        Assert.Equal(new DateFilter(DateFilterPreset.ThisMonth), new DateFilter(DateFilterPreset.ThisMonth));
        Assert.NotEqual(new DateFilter(DateFilterPreset.ThisMonth), new DateFilter(DateFilterPreset.LastMonth));
    }
}

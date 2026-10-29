using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace GroupSplit.App.Web.Test.Components;

public class DayGroupedItemsProviderTest
{
    [Theory]
    [InlineData(0, 3, true, false, true)]
    [InlineData(1, 3, false, true, false)]
    [InlineData(2, 3, true, false, false)]
    public async Task Day_headings_follow_real_boundaries_across_virtual_windows(
        int start, int count, bool first, bool second, bool third)
    {
        var dates = new[] { 29, 29, 28, 28, 28 };
        var result = await DayGroupedItemsProvider.GetAsync<int>(
            new ItemsProviderRequest(start, count, CancellationToken.None),
            (page, size, _) => Task.FromResult(new PagedResponse<int>(
                dates.Skip((page - 1) * size).Take(size).ToArray(), page, size, dates.Length)),
            day => new DateOnly(2026, 10, day));
        Assert.Equal([first, second, third], result.Items.Select(row => row.StartsDay));
        Assert.Equal(dates.Skip(start).Take(count), result.Items.Select(row => row.Entry));
        Assert.Equal(dates.Length, result.TotalItemCount);
    }
}

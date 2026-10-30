using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace GroupSplit.App.Web.Test.Components;

public class PagedItemsProviderTest
{
    [Fact]
    public async Task An_unaligned_window_crosses_pages_without_losing_items()
    {
        var calls = new List<int>();
        var result = await PagedItemsProvider.GetAsync<int>(new ItemsProviderRequest(8, 7, CancellationToken.None),
            (page, size, _) =>
            {
                calls.Add(page);
                IReadOnlyList<int> values = Enumerable.Range((page - 1) * size, Math.Min(size, 23 - (page - 1) * size)).ToArray();
                return Task.FromResult(new PagedResponse<int>(values, page, size, 23));
            }, pageSize: 10);

        Assert.Equal(Enumerable.Range(8, 7), result.Items);
        Assert.Equal(23, result.TotalItemCount);
        Assert.Equal([1, 2], calls);
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_reading_a_page()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PagedItemsProvider.GetAsync<int>(new ItemsProviderRequest(0, 10, cancellation.Token),
                (_, _, _) => throw new InvalidOperationException("Should not read")));
    }

    [Fact]
    public async Task A_window_beyond_the_end_returns_no_items_and_the_real_total()
    {
        var result = await PagedItemsProvider.GetAsync<int>(new ItemsProviderRequest(100, 20, CancellationToken.None),
            (page, size, _) => Task.FromResult(new PagedResponse<int>([], page, size, 23)), pageSize: 10);
        Assert.Empty(result.Items);
        Assert.Equal(23, result.TotalItemCount);
    }

    [Fact]
    public async Task A_short_final_page_returns_only_the_items_available()
    {
        var result = await PagedItemsProvider.GetAsync<int>(new ItemsProviderRequest(18, 10, CancellationToken.None),
            (page, size, _) =>
            {
                var start = (page - 1) * size;
                IReadOnlyList<int> values = Enumerable.Range(start, Math.Min(size, Math.Max(0, 23 - start))).ToArray();
                return Task.FromResult(new PagedResponse<int>(values, page, size, 23));
            }, pageSize: 10);
        Assert.Equal(Enumerable.Range(18, 5), result.Items);
        Assert.Equal(23, result.TotalItemCount);
    }
}

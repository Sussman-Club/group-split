using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace GroupSplit.App.Web.Test.Components;

public class PagedItemsProviderTest
{
    [Fact]
    public async Task The_cache_is_bounded_and_a_new_filter_generation_cannot_read_old_pages()
    {
        var calls = 0;
        var cache = new TransactionPageCache<int>();
        Task<PagedResponse<int>> Fetch(int page, int size, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new PagedResponse<int>([calls], page, size, 500));
        }
        for (var page = 1; page <= 9; page++) await cache.GetAsync(page, 50, CancellationToken.None, Fetch);
        await cache.GetAsync(9, 50, CancellationToken.None, Fetch);
        Assert.Equal(9, calls);
        await cache.GetAsync(1, 50, CancellationToken.None, Fetch);
        Assert.Equal(10, calls);
        cache = new();
        await cache.GetAsync(9, 50, CancellationToken.None, Fetch);
        Assert.Equal(11, calls);
    }

    [Fact]
    public async Task Day_grouping_reads_five_pages_without_cache_and_three_with_cache()
    {
        async Task<int> Count(bool cached)
        {
            var calls = 0;
            var cache = new TransactionPageCache<int>();
            Task<PagedResponse<int>> Fetch(int page, int size, CancellationToken ct)
            {
                calls++;
                return Task.FromResult(new PagedResponse<int>(Enumerable.Range((page - 1) * size, size).ToArray(), page, size, 200));
            }
            for (var start = 0; start < 150; start += 50)
            {
                var result = await DayGroupedItemsProvider.GetAsync<int>(new(start, 50, CancellationToken.None),
                    (page, size, ct) => cached ? cache.GetAsync(page, size, ct, Fetch) : Fetch(page, size, ct),
                    value => new DateOnly(2026, 1, 1).AddDays(value / 10));
                Assert.Equal(Enumerable.Range(start, 50), result.Items.Select(row => row.Entry));
            }
            return calls;
        }
        Assert.Equal(5, await Count(false));
        Assert.Equal(3, await Count(true));
    }

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

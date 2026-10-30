using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace GroupSplit.App.Shared.Services.Transactions;

public record DayGroupedItem<T>(T Entry, bool StartsDay);

public static class DayGroupedItemsProvider
{
    public static async ValueTask<ItemsProviderResult<DayGroupedItem<T>>> GetAsync<T>(
        ItemsProviderRequest request,
        Func<int, int, CancellationToken, Task<PagedResponse<T>>> fetch,
        Func<T, DateOnly> day)
    {
        // The preceding item determines whether a virtual window starts mid-day.
        var preceding = request.StartIndex > 0 ? 1 : 0;
        var window = new ItemsProviderRequest(request.StartIndex - preceding,
            request.Count + preceding, request.CancellationToken);
        var result = await PagedItemsProvider.GetAsync(window, fetch);
        var entries = result.Items.ToArray();
        var rows = entries.Skip(preceding).Select((entry, index) =>
            new DayGroupedItem<T>(entry, index + preceding == 0 ||
                day(entry) != day(entries[index + preceding - 1]))).ToArray();
        return new ItemsProviderResult<DayGroupedItem<T>>(rows, result.TotalItemCount);
    }
}

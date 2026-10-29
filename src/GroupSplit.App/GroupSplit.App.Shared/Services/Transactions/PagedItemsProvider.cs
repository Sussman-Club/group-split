using Microsoft.AspNetCore.Components.Web.Virtualization;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>Adapts Blazor's arbitrary item windows to the API's one-based pages.</summary>
public static class PagedItemsProvider
{
    public static async ValueTask<ItemsProviderResult<T>> GetAsync<T>(
        ItemsProviderRequest request,
        Func<int, int, CancellationToken, Task<PagedResponse<T>>> fetch,
        int pageSize = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.StartIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        pageSize = Math.Min(pageSize, PageRequest.MaxPageSize);
        var firstPage = request.StartIndex / pageSize + 1;
        var skip = request.StartIndex % pageSize;
        var items = new List<T>();
        var pageNumber = firstPage;
        var total = 0;

        // Even a zero-length request must return the real total for Virtualize.
        do
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var page = await fetch(pageNumber, pageSize, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            total = page.TotalCount;
            var available = page.Items.Skip(skip).Take(Math.Max(0, request.Count - items.Count));
            items.AddRange(available);
            skip = 0;
            if (page.Items.Count == 0 || (long)pageNumber * pageSize >= total) break;
            pageNumber++;
        } while (items.Count < request.Count);

        return new ItemsProviderResult<T>(items, total);
    }
}

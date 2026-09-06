using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Paging and sorting, for any listing. Both stay on <see cref="IQueryable{T}"/> so they
/// compose after whatever scoping and filtering an endpoint has already done, and so the
/// count and the page are asked of the database rather than of a list in memory.
/// </summary>
public static class QueryablePagingExtensions
{
    extension<T>(IQueryable<T> source)
    {
        /// <summary>Orders the query by one of the keys <paramref name="map"/> offers.</summary>
        public IOrderedQueryable<T> ApplySort(SortRequest? sort, SortMap<T> map) => map.Apply(source, sort);

        /// <summary>
        /// Reads one page, and how many rows there are to page through. Two round trips: the
        /// count over the filtered query, then the rows.
        /// </summary>
        /// <remarks>
        /// Expects an ordered query -- <see cref="ApplySort"/> gives one -- because
        /// <c>Skip</c> over an unordered query is not repeatable.
        /// </remarks>
        public async Task<PagedResponse<T>> ToPageAsync(PageRequest? page, CancellationToken ct)
        {
            var applied = (page ?? new PageRequest()).Normalized();

            var total = await source.CountAsync(ct);

            var items = await source
                .Skip((applied.Page - 1) * applied.PageSize)
                .Take(applied.PageSize)
                .ToListAsync(ct);

            return new PagedResponse<T>(items, applied.Page, applied.PageSize, total);
        }
    }
}

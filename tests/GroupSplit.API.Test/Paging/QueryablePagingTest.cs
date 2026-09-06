using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Paging;

/// <summary>
/// The paging and sorting every listing is built on. They are about queries the database
/// runs -- the count and the page are two round trips, not a list sliced in memory -- so
/// these go through a context of their own rather than a plain <c>AsQueryable</c>, which
/// carries no async provider for <c>CountAsync</c> to use. The rows are the test's own
/// shape: nothing here is about transactions.
/// </summary>
public class QueryablePagingTest : IAsyncLifetime
{
    private sealed class Row
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public decimal Amount { get; init; }
    }

    private sealed class RowContext(DbContextOptions<RowContext> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();
    }

    private RowContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        _context = new RowContext(new DbContextOptionsBuilder<RowContext>()
            .UseInMemoryDatabase($"PagingDb_{Guid.NewGuid()}")
            .Options);

        await _context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    /// <summary>Seeds <paramref name="count"/> rows, each dearer than the last.</summary>
    private IQueryable<Row> Rows(int count, decimal? sameAmount = null)
    {
        _context.Rows.AddRange(Enumerable.Range(1, count).Select(i => new Row
        {
            Id = i,
            Name = $"Row {i:D2}",
            Amount = sameAmount ?? i * 10m
        }));

        _context.SaveChanges();

        return _context.Rows;
    }

    private static readonly SortMap<Row> Map = new SortMap<Row>()
        .Key("amount", row => row.Amount, defaultDescending: true)
        .Key("name", row => row.Name)
        .Default("amount")
        .TieBreak(row => row.Id);

    // ---- paging ----------------------------------------------------------------------------

    [Fact]
    public async Task The_first_page_holds_a_page_of_rows_and_the_count_of_all_of_them()
    {
        var page = await Rows(30).OrderBy(row => row.Id)
            .ToPageAsync(new PageRequest(Page: 1, PageSize: 10), TestContext.Current.CancellationToken);

        Assert.Equal(10, page.Items.Count);
        Assert.Equal(1, page.Items[0].Id);
        Assert.Equal(30, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(10, page.PageSize);
    }

    [Fact]
    public async Task The_last_page_holds_what_is_left()
    {
        var page = await Rows(25).OrderBy(row => row.Id)
            .ToPageAsync(new PageRequest(Page: 3, PageSize: 10), TestContext.Current.CancellationToken);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(21, page.Items[0].Id);
        Assert.Equal(25, page.TotalCount);
    }

    /// <summary>
    /// Past the end is empty rather than an error: a client on page 4 when someone else
    /// deletes the rows under it should see an empty page and a total it can act on, not a
    /// failure.
    /// </summary>
    [Fact]
    public async Task A_page_past_the_end_is_empty_and_still_reports_the_total()
    {
        var page = await Rows(5).OrderBy(row => row.Id)
            .ToPageAsync(new PageRequest(Page: 9, PageSize: 10), TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task No_request_at_all_means_the_first_page_at_the_default_size()
    {
        var page = await Rows(100).OrderBy(row => row.Id)
            .ToPageAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(1, page.Page);
        Assert.Equal(PageRequest.DefaultPageSize, page.PageSize);
        Assert.Equal(PageRequest.DefaultPageSize, page.Items.Count);
    }

    [Fact]
    public async Task A_page_size_over_the_maximum_is_clamped_and_the_page_says_so()
    {
        var page = await Rows(500).OrderBy(row => row.Id)
            .ToPageAsync(new PageRequest(Page: 1, PageSize: 500), TestContext.Current.CancellationToken);

        Assert.Equal(PageRequest.MaxPageSize, page.PageSize);
        Assert.Equal(PageRequest.MaxPageSize, page.Items.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_page_below_the_first_one_is_the_first_one(int requested)
    {
        var page = await Rows(30).OrderBy(row => row.Id)
            .ToPageAsync(new PageRequest(Page: requested, PageSize: 10), TestContext.Current.CancellationToken);

        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.Items[0].Id);
    }

    [Fact]
    public async Task A_page_size_below_one_is_one()
    {
        var page = await Rows(30).OrderBy(row => row.Id)
            .ToPageAsync(new PageRequest(Page: 1, PageSize: 0), TestContext.Current.CancellationToken);

        Assert.Equal(1, page.PageSize);
        Assert.Single(page.Items);
    }

    // ---- sorting ---------------------------------------------------------------------------

    [Fact]
    public void No_key_means_the_default_one_in_its_own_direction()
    {
        var sorted = Rows(3).ApplySort(null, Map).ToList();

        // "amount" is declared descending by default.
        Assert.Equal([30m, 20m, 10m], sorted.Select(row => row.Amount));
    }

    [Fact]
    public void A_blank_key_is_treated_as_no_key()
    {
        var sorted = Rows(3).ApplySort(new SortRequest(SortBy: "   "), Map).ToList();

        Assert.Equal([30m, 20m, 10m], sorted.Select(row => row.Amount));
    }

    [Fact]
    public void A_key_is_matched_whatever_its_casing()
    {
        var sorted = Rows(3).ApplySort(new SortRequest(SortBy: "NaMe"), Map).ToList();

        Assert.Equal(["Row 01", "Row 02", "Row 03"], sorted.Select(row => row.Name));
    }

    [Fact]
    public void The_direction_the_caller_asks_for_wins_over_the_key_default()
    {
        var sorted = Rows(3).ApplySort(new SortRequest("amount", SortDescending: false), Map).ToList();

        Assert.Equal([10m, 20m, 30m], sorted.Select(row => row.Amount));
    }

    /// <summary>
    /// The reason a map needs a tiebreak: rows the chosen key cannot separate must not move
    /// between one request and the next, or paging shows a row twice and another never.
    /// </summary>
    [Fact]
    public async Task Rows_equal_on_the_key_keep_one_order_across_pages()
    {
        var sorted = Rows(10, sameAmount: 50m).ApplySort(new SortRequest("amount"), Map);

        var first = await sorted.ToPageAsync(new PageRequest(1, 4), TestContext.Current.CancellationToken);
        var second = await sorted.ToPageAsync(new PageRequest(2, 4), TestContext.Current.CancellationToken);
        var third = await sorted.ToPageAsync(new PageRequest(3, 4), TestContext.Current.CancellationToken);

        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(row => row.Id).ToList();

        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct().Count());
    }

    [Fact]
    public void A_key_the_listing_does_not_offer_is_a_bad_request_naming_the_ones_it_does()
    {
        var failure = Assert.Throws<ValidationException>(() =>
            Rows(3).ApplySort(new SortRequest("nonsense"), Map).ToList());

        Assert.Equal(ErrorCodes.BadRequest, failure.Code);
        Assert.Contains("amount", failure.Message);
        Assert.Contains("name", failure.Message);
    }

    /// <summary>A map with no default cannot page repeatably, so it fails as the bug it is.</summary>
    [Fact]
    public void A_map_without_a_default_is_a_mistake_in_the_code_not_in_the_request()
    {
        var incomplete = new SortMap<Row>().Key("name", row => row.Name);

        Assert.Throws<InvalidOperationException>(() => Rows(3).ApplySort(null, incomplete).ToList());
    }
}

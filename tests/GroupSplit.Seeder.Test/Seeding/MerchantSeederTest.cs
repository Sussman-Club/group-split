using System.Runtime.CompilerServices;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

/// <summary>
/// What a seeded shop turns into: a merchant row, and a URL pointing at one of the marks
/// committed beside the app.
/// </summary>
/// <remarks>
/// The URL is worth pinning because nothing else checks it. It is assembled here from a
/// name in a seed file, and the only symptom of getting the path wrong is a demo where
/// every logo is a broken image -- which looks like the feature not working rather than
/// like a typo in one string.
/// </remarks>
public class MerchantSeederTest
{
    [Fact]
    public async Task A_seeded_shop_gets_its_mark_from_the_shared_project()
    {
        var merchant = await MapAsync(new MerchantSeedDto
        {
            Id = Guid.Parse("3F2A1B4C-5D6E-4F70-8A91-000000000001"),
            Name = "Lidl",
            Logo = "lidl"
        });

        Assert.NotNull(merchant);
        Assert.Equal("Lidl", merchant.Name);
        Assert.Equal("/_content/GroupSplit.App.Shared/merchants/lidl.svg", merchant.LogoUrl);
    }

    [Fact]
    public async Task A_shop_with_no_mark_has_no_logo_rather_than_a_url_to_nothing()
    {
        var merchant = await MapAsync(new MerchantSeedDto
        {
            Id = Guid.NewGuid(),
            Name = "Pharmacy 88"
        });

        Assert.NotNull(merchant);

        // Null renders as initials, which is what a real merchant the provider has no logo
        // for looks like -- and is worth being able to seed.
        Assert.Null(merchant.LogoUrl);
    }

    [Fact]
    public async Task The_name_is_folded_the_way_a_synced_row_will_fold_it()
    {
        var merchant = await MapAsync(new MerchantSeedDto
        {
            Id = Guid.NewGuid(),
            Name = "  Blue Bottle Coffee ",
            Logo = "blue-bottle-coffee"
        });

        Assert.NotNull(merchant);

        // The key the unique index is on. A real sync that later meets this shop resolves
        // it through the same folding, so it finds this row instead of writing a second.
        Assert.Equal("Blue Bottle Coffee", merchant.Name);
        Assert.Equal("blue bottle coffee", merchant.NormalizedName);
    }

    /// <summary>
    /// The seeder's mapping, on its own. It reads nothing from the database -- a merchant
    /// row depends on nothing else -- so the context here is never connected to anything,
    /// which is also why this needs no provider.
    /// </summary>
    private static Task<Merchant?> MapAsync(MerchantSeedDto dto) =>
        new Probe(new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options),
                TimeProvider.System,
                new FakeSource<MerchantSeedDto>([dto]))
            .Map(dto);

    private sealed class Probe(AppDbContext db, TimeProvider clock, ISeedDataSource<MerchantSeedDto> source)
        : MerchantSeeder(db, clock, NullLogger<MerchantSeeder>.Instance, source)
    {
        public Task<Merchant?> Map(MerchantSeedDto dto) => MapAsync(dto);
    }

    private sealed class FakeSource<T>(IReadOnlyList<T> items) : ISeedDataSource<T>
    {
        public async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }
}

using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Seeds the shops the demo data spends money at, each with a mark to show for it.
/// </summary>
/// <remarks>
/// Without these nobody can see what the merchant column is for. A real account fills this
/// table from a bank, and an account with no bank linked -- which is everybody reviewing
/// the app -- would otherwise see every expense as the payer's initials and never know
/// there was anything else to see.
/// <para>
/// The marks are ours: simple glyph tiles under <c>wwwroot/merchants</c>, not the shops'
/// own logos. A real logo arrives from the provider with the row that names it, and
/// committing somebody's trademark to the repository to fake that is not the same thing.
/// They are files rather than links to a logo service for the ordinary reason: a demo that
/// needs the internet to render is a demo that renders as broken images on the day the
/// service moves.
/// </para>
/// <para>
/// Depends on nothing, and everything that points at a merchant depends on it. Both the
/// expenses and the bank rows name these shops, those two seeders run at the same time as
/// each other, and neither may be the one creating the row.
/// </para>
/// </remarks>
public class MerchantSeeder(
    AppDbContext db,
    TimeProvider clock,
    ILogger<MerchantSeeder> logger,
    ISeedDataSource<MerchantSeedDto> source)
    : AppDbContextSeeder<Merchant, MerchantSeedDto>(db, source, logger)
{
    /// <summary>
    /// Where the shared project's static files are served from, in every client: the web
    /// app and the MAUI one both address a razor class library's <c>wwwroot</c> this way.
    /// Root-relative, so it resolves against whichever of them is doing the rendering.
    /// </summary>
    private const string MarkPath = "/_content/GroupSplit.App.Shared/merchants";

    protected override Task<Merchant?> MapAsync(MerchantSeedDto dto, CancellationToken ct = default)
    {
        var name = dto.Name.Trim();

        return Task.FromResult<Merchant?>(new Merchant
        {
            Id = dto.Id,
            Name = name,
            // Folded the same way the resolver folds a provider's name, so a real sync that
            // later meets one of these shops finds the seeded row instead of a second one.
            NormalizedName = name.ToLowerInvariant(),
            LogoUrl = dto.Logo is { Length: > 0 } mark ? $"{MarkPath}/{mark}.svg" : null,
            FirstSeenAt = clock.GetUtcNow().AddDays(-30)
        });
    }
}

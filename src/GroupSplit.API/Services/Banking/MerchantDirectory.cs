using System.Diagnostics.CodeAnalysis;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Turns the name a provider puts on a row into the one shared <see cref="Merchant"/> that
/// stands for that place.
/// </summary>
public interface IMerchantDirectory
{
    /// <summary>
    /// The merchant for a provider's name and logo, creating it the first time that place
    /// is seen, or null when the provider named no merchant at all.
    /// </summary>
    /// <remarks>
    /// Added to the change tracker and not saved: the caller owns the transaction, and a
    /// sync saves its rows a page at a time. Calling this twice for the same name in one
    /// scope returns the same instance both times, saved or not.
    /// </remarks>
    Task<Merchant?> ResolveAsync(string? name, string? logoUrl, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// Scoped, and the cache with it: one sync run meets the same shop on dozens of rows, and
/// this is what makes that one insert and dozens of links rather than a query per row.
/// <para>
/// Two concurrent syncs can still race to create the same merchant -- both look, neither
/// finds, both insert -- and the unique index on the normalized name is what catches it.
/// The loser's <c>SaveChanges</c> throws, the run reports itself
/// <see cref="SyncOutcome.Interrupted"/>, and the next run over the same pages finds the
/// row the winner wrote. The cursor did not move, so nothing is lost by that; holding a
/// lock over a table shared by every connection would cost more than the collision does.
/// </para>
/// </remarks>
public sealed class MerchantDirectory(AppDbContext dbContext, TimeProvider clock) : IMerchantDirectory
{
    /// <summary>
    /// Keyed by the normalized name, which is the column the unique index is on -- so the
    /// cache can only ever agree with the database about what counts as the same place.
    /// </summary>
    private readonly Dictionary<string, Merchant> _seen = [];

    public async Task<Merchant?> ResolveAsync(string? name, string? logoUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var display = Clip(name.Trim(), 128);
        var normalized = display.ToLowerInvariant();

        if (_seen.TryGetValue(normalized, out var cached))
            return Freshen(cached, logoUrl);

        var stored = await dbContext.Set<Merchant>()
            .FirstOrDefaultAsync(merchant => merchant.NormalizedName == normalized, ct);

        if (stored is not null)
        {
            _seen[normalized] = stored;
            return Freshen(stored, logoUrl);
        }

        var created = new Merchant
        {
            Name = display,
            NormalizedName = normalized,
            LogoUrl = Clip(logoUrl, 512),
            FirstSeenAt = clock.GetUtcNow()
        };

        dbContext.Add(created);
        _seen[normalized] = created;

        return created;
    }

    /// <summary>
    /// A logo the provider has now for a place we had none for. The reason the ledger holds
    /// a link and not a copy: this lights up every expense already filed against the place,
    /// and it is why the cache hands back the tracked entity rather than a snapshot.
    /// </summary>
    /// <remarks>
    /// One direction only. A provider that stops sending a logo it used to send is far more
    /// likely to be having a bad afternoon than to be telling us the shop lost its sign, and
    /// blanking the column on that would take the logo off every row at once.
    /// </remarks>
    private static Merchant Freshen(Merchant merchant, string? logoUrl)
    {
        if (merchant.LogoUrl is null && !string.IsNullOrWhiteSpace(logoUrl))
            merchant.LogoUrl = Clip(logoUrl, 512);

        return merchant;
    }

    /// <summary>
    /// The columns have lengths and the provider's strings do not, the same way the imported
    /// row's own text is clipped.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? Clip(string? value, int maxLength) =>
        value is { Length: var length } && length > maxLength ? value[..maxLength] : value;
}

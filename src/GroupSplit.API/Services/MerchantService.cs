using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IMerchantService
{
    Task<IQueryable<Merchant>> List(string? search, CancellationToken ct = default);
    Task<Merchant> Get(Guid id, CancellationToken ct = default);
    Task<Merchant> Create(CreateMerchantRequest request, CancellationToken ct = default);
    Task<Merchant> Update(Guid id, UpdateMerchantRequest request, CancellationToken ct = default);
    Task Delete(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The places money gets spent, by hand. The sync writes this table on its own from what a
/// provider says; this is the other way in.
/// </summary>
/// <remarks>
/// Unscoped, and the one service here that is. Every other read in the app is narrowed to
/// the caller's groups because the rows belong to somebody; a merchant belongs to nobody --
/// "Lidl" is not a fact about a group, and two groups that both shop there share the row.
/// So there is nothing to scope to, and nothing here leaks: a merchant's name and logo are
/// what a bank would have told either group anyway, and the row says nothing about who
/// spent what.
/// <para>
/// Editing is deliberately not a per-group thing for the same reason: renaming Lidl renames
/// it everywhere, which is why <see cref="MerchantResponse.TransactionCount"/> is on the
/// response -- a caller about to rename a place with four hundred expenses behind it should
/// be told so first.
/// </para>
/// </remarks>
public class MerchantService(AppDbContext dbContext, TimeProvider clock) : IMerchantService
{
    public Task<IQueryable<Merchant>> List(string? search, CancellationToken ct = default)
    {
        var query = dbContext.Set<Merchant>().AsQueryable();

        // Lowered here rather than in the expression, the way every other search in the app
        // does it: it keeps the comparison off the database's collation and translates on
        // both the real provider and the in-memory one the tests use.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLowerInvariant();
            query = query.Where(merchant => merchant.NormalizedName.Contains(needle));
        }

        return Task.FromResult(query);
    }

    public async Task<Merchant> Get(Guid id, CancellationToken ct = default) =>
        await dbContext.Set<Merchant>().FirstOrDefaultAsync(merchant => merchant.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");

    public async Task<Merchant> Create(CreateMerchantRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        var normalized = name.ToLowerInvariant();

        await RefuseDuplicateName(normalized, null, ct);

        var merchant = new Merchant
        {
            Name = name,
            // The same folding the sync's resolver uses, so a provider that later reports
            // this shop finds the row somebody typed instead of writing a second one.
            NormalizedName = normalized,
            LogoUrl = Blank(request.LogoUrl),
            FirstSeenAt = clock.GetUtcNow()
        };

        dbContext.Add(merchant);
        await dbContext.SaveChangesAsync(ct);

        return merchant;
    }

    public async Task<Merchant> Update(Guid id, UpdateMerchantRequest request, CancellationToken ct = default)
    {
        var merchant = await Get(id, ct);
        var name = request.Name.Trim();
        var normalized = name.ToLowerInvariant();

        await RefuseDuplicateName(normalized, id, ct);

        merchant.Name = name;
        merchant.NormalizedName = normalized;
        merchant.LogoUrl = Blank(request.LogoUrl);

        await dbContext.SaveChangesAsync(ct);

        return merchant;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var merchant = await Get(id, ct);

        var stillUsed = await dbContext.Set<Transaction>()
                            .AnyAsync(transaction => transaction.MerchantId == id, ct)
                        || await dbContext.Set<BankTransaction>()
                            .AnyAsync(row => row.MerchantId == id, ct);

        // The database would refuse it anyway -- both relationships are Restrict, so a
        // merchant cannot take somebody's history with it -- but a foreign-key violation is
        // not something a person can act on. Imported rows count as well as expenses: a row
        // waiting in an inbox still points here, and deleting under it would leave the inbox
        // unable to render what it is offering.
        if (stillUsed)
            throw new ConflictException(ErrorCodes.MerchantInUse,
                "This merchant still has transactions pointing at it.");

        dbContext.Remove(merchant);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One row per place is the whole point of the table, and the unique index on the folded
    /// name is what holds it. Checked here as well so the refusal can say which name rather
    /// than which constraint.
    /// </summary>
    private async Task RefuseDuplicateName(string normalized, Guid? excluding, CancellationToken ct)
    {
        var taken = await dbContext.Set<Merchant>().AnyAsync(merchant =>
            merchant.Id != excluding && merchant.NormalizedName == normalized, ct);

        if (taken)
            throw new ConflictException(ErrorCodes.MerchantNameTaken,
                $"There is already a merchant called \"{normalized}\".");
    }

    /// <summary>
    /// Whitespace is no logo. An empty string is nobody's URL and would render as a broken
    /// image, so it is stored as the null it means.
    /// </summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

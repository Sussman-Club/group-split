using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public partial class MerchantService
{
    private const int DirectoryPageSize = 30;

    public async Task<MerchantResponse> GetDirectory(Guid id, CancellationToken ct = default) =>
        await dbContext.Set<Merchant>().AsNoTracking().Where(m => m.Id == id || dbContext.Set<MerchantRedirect>().Any(r => r.Id == id && r.MerchantId == m.Id))
            .Select(m => new MerchantResponse(m.Id, m.Name, m.LogoUrl, m.FirstSeenAt, m.Transactions.Count) { Version = m.Version })
            .SingleOrDefaultAsync(ct)
        ?? throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");

    public Task<DirectoryPage<MerchantResponse>> ListDirectory(string? search, int? page, CancellationToken ct = default) => ListDirectory(search, page, null, ct);

    public async Task<DirectoryPage<MerchantResponse>> ListDirectory(string? search, int? page, string? sort, CancellationToken ct)
    {
        if (page is < 0 or > 10000 || search?.Length > 200)
        {
            throw new ValidationException(ErrorCodes.InvalidMerchant, "Check the merchant fields and filters.");
        }

        var query = dbContext.Set<Merchant>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLowerInvariant();
            query = query.Where(m => m.NormalizedName.Contains(needle) || dbContext.Set<MerchantAlias>().Any(a => a.MerchantId == m.Id && a.NormalizedName.Contains(needle)));
        }
        if (sort == "usage")
        {
            // Aggregate once before sorting, instead of evaluating a correlated count
            // separately for every merchant in the directory.
            var counts = dbContext.Set<Transaction>().AsNoTracking().Where(t => t.MerchantId != null)
                .GroupBy(t => t.MerchantId).Select(g => new { MerchantId = g.Key, Count = (int?)g.Count() });
            var ranked = from merchant in query
                         join count in counts on (Guid?)merchant.Id equals count.MerchantId into joined
                         from count in joined.DefaultIfEmpty()
                         orderby (count.Count ?? 0) descending, merchant.Id
                         select new MerchantResponse(merchant.Id, merchant.Name, merchant.LogoUrl, merchant.FirstSeenAt,
                             count.Count ?? 0)
                         {
                             Version = merchant.Version
                         };
            var pageRows = await ranked.Skip((page ?? 0) * DirectoryPageSize).Take(DirectoryPageSize + 1).ToListAsync(ct);
            return new DirectoryPage<MerchantResponse>(pageRows.Take(DirectoryPageSize).ToList(), pageRows.Count > DirectoryPageSize);
        }
        var ordered = sort switch
        {
            null or "" or "name" => query.OrderBy(m => m.Name).ThenBy(m => m.Id),
            "newest" => query.OrderByDescending(m => m.FirstSeenAt).ThenBy(m => m.Id),
            _ => throw new ValidationException(ErrorCodes.BadRequest, "Choose a valid merchant sort.")
        };
        var items = await ordered
            .Skip((page ?? 0) * DirectoryPageSize).Take(DirectoryPageSize + 1)
            .Select(m => new MerchantResponse(m.Id, m.Name, m.LogoUrl, m.FirstSeenAt, m.Transactions.Count) { Version = m.Version })
            .ToListAsync(ct);
        return new DirectoryPage<MerchantResponse>(items.Take(DirectoryPageSize).ToList(), items.Count > DirectoryPageSize);
    }

    public Task<MerchantResponse> CreateDirectory(CreateMerchantRequest request, CancellationToken ct = default) => CreateDirectory(request, null, ct);

    public async Task<MerchantResponse> CreateDirectory(CreateMerchantRequest request, Guid? actorId, CancellationToken ct)
    {
        if (!ValidMerchant(request.Name, request.LogoUrl))
        {
            throw new ValidationException(ErrorCodes.InvalidMerchant, "Check the merchant fields and filters.");
        }

        var name = request.Name.Trim();
        var normalized = name.ToLowerInvariant();
        if (await dbContext.Set<MerchantAlias>().AnyAsync(m => m.NormalizedName == normalized, ct) || await dbContext.Set<Merchant>().AnyAsync(m => m.NormalizedName == normalized, ct))
        {
            throw new ConflictException(ErrorCodes.MerchantNameExists, "A merchant with this name already exists.");
        }

        var merchant = new Merchant
        {
            Name = name,
            NormalizedName = normalized,
            LogoUrl = AllowedLogo(request.LogoUrl),
            FirstSeenAt = clock.GetUtcNow()
        };
        dbContext.Add(merchant);
        StaffActivityService.Record(dbContext, actorId, "Merchants", merchant.Id, "Merchant created", clock.GetUtcNow(),
            new("Name", null, name), new("Image", null, merchant.LogoUrl));
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException(ErrorCodes.MerchantNameExists, "A merchant with this name already exists.");
        }
        return new MerchantResponse(merchant.Id, merchant.Name, merchant.LogoUrl, merchant.FirstSeenAt, 0) { Version = merchant.Version };
    }

    public Task UpdateDirectory(Guid id, UpdateMerchantRequest request, CancellationToken ct = default) => UpdateDirectory(id, request, null, ct);

    public async Task UpdateDirectory(Guid id, UpdateMerchantRequest request, Guid? actorId, CancellationToken ct)
    {
        if (!ValidMerchant(request.Name, request.LogoUrl))
        {
            throw new ValidationException(ErrorCodes.InvalidMerchant, "Check the merchant fields and filters.");
        }

        var merchant = await dbContext.Set<Merchant>().FindAsync([id], ct);
        if (merchant is null)
        {
            throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");
        }

        if (request.ExpectedVersion is { } expected && merchant.Version != expected)
        {
            throw new ConflictException(ErrorCodes.Conflict, "This merchant changed. Reload before saving.");
        }

        var normalized = request.Name.Trim().ToLowerInvariant();
        if (await dbContext.Set<MerchantAlias>().AnyAsync(a => a.MerchantId != id && a.NormalizedName == normalized, ct) || await dbContext.Set<Merchant>().AnyAsync(m => m.Id != id && m.NormalizedName == normalized, ct))
        {
            throw new ConflictException(ErrorCodes.MerchantNameExists, "A merchant with this name already exists.");
        }

        var changes = new List<ActivityChange>();
        if (merchant.Name != request.Name.Trim())
        {
            changes.Add(new("Name", merchant.Name, request.Name.Trim()));
        }

        if (merchant.LogoUrl != AllowedLogo(request.LogoUrl))
        {
            changes.Add(new("Image", merchant.LogoUrl, AllowedLogo(request.LogoUrl)));
        }

        if (changes.Count == 0)
        {
            return;
        }

        merchant.Version = Guid.NewGuid();
        StaffActivityService.Record(dbContext, actorId, "Merchants", id, "Merchant updated", clock.GetUtcNow(), changes.ToArray());
        merchant.Name = request.Name.Trim();
        merchant.NormalizedName = normalized;
        merchant.LogoUrl = AllowedLogo(request.LogoUrl);
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.Conflict, "This merchant changed. Reload before saving."); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException(ErrorCodes.MerchantNameExists, "A merchant with this name already exists.");
        }
    }

    private static bool ValidMerchant(string? name, string? logo) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && (logo is null || logo.Length <= 512)
        && (string.IsNullOrWhiteSpace(logo) || MerchantLogos.IsAllowed(logo));
}

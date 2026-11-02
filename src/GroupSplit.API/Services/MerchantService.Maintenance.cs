using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public partial class MerchantService
{
    public async Task<List<MerchantAliasResponse>> Aliases(Guid id, CancellationToken ct)
    {
        var merchant = await GetDirectory(id, ct);
        return await dbContext.Set<MerchantAlias>().AsNoTracking().Where(a => a.MerchantId == merchant.Id)
            .OrderBy(a => a.Name).Select(a => new MerchantAliasResponse(a.Id, a.Name, a.NormalizedName == merchant.Name.ToLower())).ToListAsync(ct);
    }

    public async Task AddAlias(Guid id, string name, Guid actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ValidationException(ErrorCodes.InvalidMerchant, "Enter an alias up to 128 characters.");
        var merchant = await dbContext.Set<Merchant>().FindAsync([id], ct)
            ?? throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");
        var normalized = name.Trim().ToLowerInvariant();
        if (await dbContext.Set<MerchantAlias>().AnyAsync(a => a.NormalizedName == normalized, ct))
            throw new ConflictException(ErrorCodes.MerchantNameExists, "This name already belongs to a merchant. Review or merge the entries instead.");
        dbContext.Add(new MerchantAlias { MerchantId = id, Name = name.Trim(), NormalizedName = normalized, CreatedAt = clock.GetUtcNow() });
        merchant.Version = Guid.NewGuid();
        StaffActivityService.Record(dbContext, actorId, "Merchants", id, "Alias added", clock.GetUtcNow(), new ActivityChange("Alias", null, name.Trim()));
        await SaveMaintenance(ct);
    }

    public async Task RemoveAlias(Guid id, Guid aliasId, Guid actorId, CancellationToken ct)
    {
        var merchant = await dbContext.Set<Merchant>().FindAsync([id], ct)
            ?? throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");
        var alias = await dbContext.Set<MerchantAlias>().SingleOrDefaultAsync(a => a.Id == aliasId && a.MerchantId == id, ct)
            ?? throw new NotFoundException(ErrorCodes.NotFound, "Alias not found.");
        if (alias.NormalizedName == merchant.NormalizedName)
            throw new ValidationException(ErrorCodes.InvalidMerchant, "The current merchant name cannot be removed as an alias.");
        dbContext.Remove(alias);
        merchant.Version = Guid.NewGuid();
        StaffActivityService.Record(dbContext, actorId, "Merchants", id, "Alias removed", clock.GetUtcNow(), new ActivityChange("Alias", alias.Name, null));
        await SaveMaintenance(ct);
    }

    public async Task<MerchantMergePreview> PreviewMerge(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        if (sourceId == targetId) throw new ValidationException(ErrorCodes.InvalidMerchant, "Choose a different target merchant.");
        var source = await GetDirectory(sourceId, ct);
        var target = await GetDirectory(targetId, ct);
        if (source.Id != sourceId || target.Id != targetId)
            throw new ConflictException(ErrorCodes.Conflict, "A merchant has already been merged. Reload the directory.");
        return new(source, target, await dbContext.Set<Transaction>().CountAsync(t => t.MerchantId == sourceId, ct),
            await dbContext.Set<BankTransaction>().CountAsync(t => t.MerchantId == sourceId, ct),
            await dbContext.Set<MerchantAlias>().CountAsync(t => t.MerchantId == sourceId, ct));
    }

    public async Task<MerchantResponse> Merge(Guid sourceId, MergeMerchantsRequest request, Guid actorId, CancellationToken ct)
    {
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(ct) : null;
        // PostgreSQL foreign-key checks take a key-share lock on the merchant. Lock both
        // rows first so references cannot be added between the impact check and the merge.
        if (dbContext.Database.IsRelational())
            await dbContext.Set<Merchant>().FromSqlInterpolated($"SELECT * FROM \"Merchant\" WHERE \"Id\" IN ({sourceId}, {request.TargetId}) ORDER BY \"Id\" FOR UPDATE").ToListAsync(ct);
        var preview = await PreviewMerge(sourceId, request.TargetId, ct);
        if (preview.Source.Version != request.SourceVersion || preview.Target.Version != request.TargetVersion)
            throw new ConflictException(ErrorCodes.Conflict, "A merchant changed after the preview. Review the merge again.");
        if (preview.Transactions != request.Transactions || preview.BankRows != request.BankRows || preview.Aliases != request.Aliases)
            throw new ConflictException(ErrorCodes.Conflict, "The merge impact changed. Review the merge again.");
        var source = await dbContext.Set<Merchant>().FindAsync([sourceId], ct);
        var target = await dbContext.Set<Merchant>().FindAsync([request.TargetId], ct);
        dbContext.Entry(source!).Property(m => m.Version).OriginalValue = request.SourceVersion;
        dbContext.Entry(target!).Property(m => m.Version).OriginalValue = request.TargetVersion;
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Set<Transaction>().Where(t => t.MerchantId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.MerchantId, request.TargetId), ct);
            await dbContext.Set<BankTransaction>().Where(t => t.MerchantId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.MerchantId, request.TargetId), ct);
            await dbContext.Set<MerchantAlias>().Where(t => t.MerchantId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.MerchantId, request.TargetId), ct);
            await dbContext.Set<MerchantRedirect>().Where(t => t.MerchantId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.MerchantId, request.TargetId), ct);
        }
        else
        {
            foreach (var row in await dbContext.Set<Transaction>().Where(t => t.MerchantId == sourceId).ToListAsync(ct)) row.MerchantId = request.TargetId;
            foreach (var row in await dbContext.Set<BankTransaction>().Where(t => t.MerchantId == sourceId).ToListAsync(ct)) row.MerchantId = request.TargetId;
            foreach (var row in await dbContext.Set<MerchantAlias>().Where(t => t.MerchantId == sourceId).ToListAsync(ct)) { row.MerchantId = request.TargetId; row.Merchant = target!; }
            foreach (var row in await dbContext.Set<MerchantRedirect>().Where(t => t.MerchantId == sourceId).ToListAsync(ct)) row.MerchantId = request.TargetId;
        }
        dbContext.Add(new MerchantRedirect { Id = sourceId, MerchantId = request.TargetId });
        target!.Version = Guid.NewGuid();
        dbContext.Remove(source!);
        var changes = new[] { new ActivityChange("Source merchant", preview.Source.Name, preview.Target.Name),
            new ActivityChange("Transactions moved", null, preview.Transactions.ToString()), new ActivityChange("Bank rows moved", null, preview.BankRows.ToString()) };
        StaffActivityService.Record(dbContext, actorId, "Merchants", sourceId, "Merchant merged", clock.GetUtcNow(), changes);
        StaffActivityService.Record(dbContext, actorId, "Merchants", request.TargetId, "Merchant merged", clock.GetUtcNow(), changes);
        await SaveMaintenance(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetDirectory(request.TargetId, ct);
    }

    private async Task SaveMaintenance(CancellationToken ct)
    {
        try { await dbContext.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.Conflict, "Records changed. Reload and review again."); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" or "23503" })
        { throw new ConflictException(ErrorCodes.Conflict, "A name or reference changed. Reload and review again."); }
    }
}

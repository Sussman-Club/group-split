using System.Text.Json;
using GroupSplit.Seeder.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders.Base;

public class DbContextSeeder<TEntity, TDto>(
    DbContext db,
    ISeedDataSource<TDto> source,
    ILogger<DbContextSeeder<TEntity, TDto>> logger)
    : ISeeder
    where TEntity : class
{
    protected DbContext DbContext => db;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var count = 0;
        await foreach (var dto in source.ReadAsync(ct))
        {
            var entity = await MapAsync(dto, ct);
            if (entity is null) continue;
            if (await ExistsAsync(entity, ct)) continue;
            await AddEntityAsync(entity, dto, ct);
            count++;
        }

        if (count > 0) await SaveAsync(ct);
        logger.LogInformation("Seeded {Count} {Entity}", count, typeof(TEntity).Name);
    }

    protected virtual Task<TEntity?> MapAsync(TDto dto, CancellationToken ct = default)
    {
        return Task.FromResult(JsonSerializer.SerializeToNode(dto).Deserialize<TEntity>());
    }

    protected virtual Task<bool> ExistsAsync(TEntity entity, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    protected virtual Task AddEntityAsync(TEntity entity, TDto dto, CancellationToken ct = default)
    {
        DbContext.Set<TEntity>().Add(entity);
        return Task.CompletedTask;
    }

    protected virtual async Task SaveAsync(CancellationToken ct = default)
    {
        await DbContext.SaveChangesAsync(ct);
    }
}

public class DbContextSeeder<TEntity, TDto, TDbContext>(
    TDbContext db,
    ISeedDataSource<TDto> source,
    ILogger<DbContextSeeder<TEntity, TDto, TDbContext>> logger)
    : DbContextSeeder<TEntity, TDto>(db, source, logger)
    where TDbContext : DbContext
    where TEntity : class
{
    protected new TDbContext DbContext => db;
}
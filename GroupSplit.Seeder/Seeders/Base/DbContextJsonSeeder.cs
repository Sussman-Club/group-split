using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders.Base;

public class DbContextJsonSeeder<TEntity, TDto>(
    DbContext db,
    string jsonPath,
    ILogger<DbContextJsonSeeder<TEntity, TDto>> logger,
    int order = 0)
    : JsonSeeder<TEntity, TDto>(jsonPath, logger, order)
    where TEntity : class
{
    protected DbContext DbContext => db;

    protected override async Task SaveAsync(CancellationToken ct = default)
    {
        await DbContext.SaveChangesAsync(ct);
    }

    protected override Task AddEntityAsync(TEntity entity, TDto dto, CancellationToken ct = default)
    {
        DbContext.Set<TEntity>().Add(entity);
        return Task.CompletedTask;
    }

    protected override Task<TEntity?> ConvertEntityAsync(TDto dto, CancellationToken ct = default)
    {
        return Task.FromResult(JsonSerializer.SerializeToNode(dto).Deserialize<TEntity>());
    }
}

public class DbContextJsonSeeder<TEntity, TDto, TDbContext>(
    TDbContext db,
    string jsonPath,
    ILogger<DbContextJsonSeeder<TEntity, TDto, TDbContext>> logger,
    int order = 0)
    : DbContextJsonSeeder<TEntity, TDto>(db, jsonPath, logger, order)
    where TDbContext : DbContext
    where TEntity : class
{
    protected new TDbContext DbContext => db;
}
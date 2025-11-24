using GroupSplit.Data;
using GroupSplit.Data.Entities;

namespace GroupSplit.Seeder.Seeders.Base;

public class AppDbContextJsonSeeder<TEntity, TDto>(
    AppDbContext db,
    string jsonPath,
    ILogger<AppDbContextJsonSeeder<TEntity, TDto>> logger)
    : DbContextJsonSeeder<TEntity, TDto, AppDbContext>(db, jsonPath, logger)
    where TEntity : Entity
{
    protected override async Task AddEntityAsync(TEntity entity, TDto dto, CancellationToken ct = default)
    {
        var existing = await DbContext.Set<TEntity>().FindAsync([entity.Id], ct);
        if (existing is not null)
        {
            logger.LogWarning("Entity {Entity} with id {Id} already exists. Skipping.", typeof(TEntity).Name,
                entity.Id);
            return;
        }

        await base.AddEntityAsync(entity, dto, ct);
    }
}
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;

namespace GroupSplit.Seeder.Seeders.Base;

public class AppDbContextSeeder<TEntity, TDto>(
    AppDbContext db,
    ISeedDataSource<TDto> source,
    ILogger<AppDbContextSeeder<TEntity, TDto>> logger)
    : DbContextSeeder<TEntity, TDto, AppDbContext>(db, source, logger)
    where TEntity : Entity
{
    protected override async Task<bool> ExistsAsync(TEntity entity, CancellationToken ct)
    {
        return await DbContext.Set<TEntity>().FindAsync([entity.Id], ct) is not null;
    }
}
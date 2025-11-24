using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Seeder.Seeders.Base;

public class JsonSeeder<TEntity, TContext>(TContext db, string jsonPath, ILogger logger)
    : JsonSeeder<TEntity, TEntity, TContext>(db, jsonPath, logger)
    where TEntity : class
    where TContext : DbContext;

public class JsonSeeder<TEntity, TDto, TContext>(TContext db, string jsonPath, ILogger logger) : IDatabaseSeeder
    where TEntity : class
    where TDto : class
    where TContext : DbContext
{
    public virtual int Order => 0;
    
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("File not found: {JsonPath}", jsonPath);
            return;
        }

        logger.LogInformation("Loading {JsonPath}", jsonPath);

        var json = await File.ReadAllTextAsync(jsonPath, ct);

        var dtos = JsonSerializer.Deserialize<List<TDto>>(json);

        if (dtos is null || dtos.Count == 0)
        {
            logger.LogWarning("No items found in {JsonPath}", jsonPath);
            return;
        }

        var entities = new List<TEntity>();

        foreach (var dto in dtos)
        {
            var entity = await ConvertEntityAsync(dto, ct);
            if (entity != null) 
                entities.Add(entity);
        }

        db.Set<TEntity>().AddRange(entities);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded {Count} {Type} entities.", entities.Count, typeof(TEntity).Name);
    }

    protected virtual Task<TEntity?> ConvertEntityAsync(TDto? dto, CancellationToken ct = default)
    {
        return Task.FromResult(dto switch
        {
            null => null,
            _ => JsonSerializer.Deserialize<TEntity>(JsonSerializer.Serialize(dto))
        });
    }
}
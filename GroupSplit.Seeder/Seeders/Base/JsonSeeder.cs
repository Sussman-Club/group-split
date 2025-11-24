using System.Text.Json;

namespace GroupSplit.Seeder.Seeders.Base;

public abstract class JsonSeeder<TEntity, TDto>(
    string jsonPath,
    ILogger<JsonSeeder<TEntity, TDto>> logger,
    int order = 0) : IDatabaseSeeder
{
    public int Order => order;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("File not found: {JsonPath}", jsonPath);
            return;
        }

        logger.LogInformation("Loading {JsonPath}", jsonPath);

        await using var stream = File.OpenRead(jsonPath);

        var dtos = JsonSerializer.DeserializeAsyncEnumerable<TDto>(stream, cancellationToken: ct);

        var elementsCount = 0;

        await foreach (var dto in dtos)
        {
            if (dto is null) continue;
            var entity = await ConvertEntityAsync(dto, ct);
            if (entity is null) continue;
            await AddEntityAsync(entity, dto, ct);
            elementsCount++;
        }

        if (elementsCount is 0)
        {
            logger.LogWarning("No items found in {JsonPath}", jsonPath);
            return;
        }

        await SaveAsync(ct);

        logger.LogInformation("Seeded {Count} {Type} entities.", elementsCount, typeof(TEntity).Name);
    }

    protected virtual Task SaveAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    protected abstract Task AddEntityAsync(TEntity entity, TDto dto, CancellationToken ct = default);
    protected abstract Task<TEntity?> ConvertEntityAsync(TDto dto, CancellationToken ct = default);
}
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GroupSplit.Seeder.Seeders.Base;

public interface ISeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public interface ISeedDataSource<TDto>
{
    IAsyncEnumerable<TDto> ReadAsync(CancellationToken ct = default);
}

public sealed class JsonArrayFileDataSource<TDto>(string path, ILogger<JsonArrayFileDataSource<TDto>> logger)
    : ISeedDataSource<TDto>
{
    public async IAsyncEnumerable<TDto> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning("Seed file not found: {Path}", path);
            yield break;
        }

        await using var stream = File.OpenRead(path);
        var dtos = JsonSerializer.DeserializeAsyncEnumerable<TDto>(stream, cancellationToken: ct);
        await foreach (var dto in dtos)
        {
            if (dto is not null) yield return dto;
        }
    }
}
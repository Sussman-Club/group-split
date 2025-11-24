using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Seeder;

public class DatabaseSeederRunner
{
    private readonly IEnumerable<IDatabaseSeeder> _seeders;
    private readonly ILogger<DatabaseSeederRunner> _logger;

    public DatabaseSeederRunner(IEnumerable<IDatabaseSeeder> seeders, ILogger<DatabaseSeederRunner> logger)
    {
        _seeders = seeders.OrderBy(s => s.Order);
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting database seeding with {Count} seeders...", _seeders.Count());

        foreach (var seeder in _seeders)
        {
            var typeName = seeder.GetType().Name;
            try
            {
                _logger.LogInformation("Seeding {Seeder} (Order {Order})...", typeName, seeder.Order);
                await seeder.SeedAsync(ct);
                _logger.LogInformation("Seeder {Seeder} completed successfully.", typeName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Seeding cancelled for {Seeder}", typeName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Seeder {Seeder} failed", typeName);
                throw;
            }
        }

        _logger.LogInformation("Database seeding completed.");
    }
}
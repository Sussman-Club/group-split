using GroupSplit.Seeder.Abstractions;

namespace GroupSplit.Seeder.Orchestration;

public class DatabaseSeederRunner(
    ILogger<DatabaseSeederRunner> logger,
    IHostApplicationLifetime applicationLifetime,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var seeders = scope.ServiceProvider.GetServices<ISeeder>().ToList();
        logger.LogInformation("Discovered {Count} seeders: {SeederList}",
            seeders.Count,
            string.Join(", ", seeders.Select(s => s.GetType().Name)));

        var layers = seeders.TopologicallySort();
        logger.LogInformation("Topological order determined with {LayerCount} layers.", layers.Count);

        foreach (var (group, depth) in layers.Select((x, i) => (x, i)))
        {
            logger.LogInformation(
                "Starting batch #{Order} with {Count} seeders: {Seeders}",
                depth + 1,
                group.Count,
                string.Join(", ", group.Select(s => s.GetType().Name)));

            var tasks = group.Select(async seeder =>
            {
                var name = seeder.GetType().Name;

                try
                {
                    await seeder.SeedAsync(stoppingToken);
                    logger.LogInformation("Seeder {Seeder} completed", name);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Seeder {Seeder} cancelled", name);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Seeder {Seeder} failed", name);
                    throw;
                }
            });

            await Task.WhenAll(tasks);
            logger.LogInformation("Batch #{Order} completed.", depth + 1);
        }

        logger.LogInformation("All database seeding completed.");
        applicationLifetime.StopApplication();
    }
}
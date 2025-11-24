using GroupSplit.Seeder.Seeders.Base;

namespace GroupSplit.Seeder;

public class DatabaseSeederRunner(
    ILogger<DatabaseSeederRunner> logger,
    IHostApplicationLifetime applicationLifetime,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var seeders = scope.ServiceProvider.GetServices<IDatabaseSeeder>();
        
        // Group seeders by their Order
        var orderedGroups = seeders
            .GroupBy(s => s.Order)
            .OrderBy(g => g.Key);
        
        foreach (var group in orderedGroups)
        {
            var order = group.Key;
            logger.LogInformation("Running seeders with Order {Order}...", order);

            // Run all seeders with this order in parallel
            var tasks = group.Select(async seeder =>
            {
                var typeName = seeder.GetType().Name;

                try
                {
                    logger.LogInformation("Seeding {Seeder} (Order {Order})...", typeName, seeder.Order);
                    await seeder.SeedAsync(stoppingToken);
                    logger.LogInformation("Seeder {Seeder} completed successfully.", typeName);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Seeding cancelled for {Seeder}", typeName);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Seeder {Seeder} failed", typeName);
                    throw;
                }
            });

            // Wait for all seeders in the same order
            await Task.WhenAll(tasks);

            logger.LogInformation("All seeders with Order {Order} completed.", order);
        }

        logger.LogInformation("Database seeding completed.");
        applicationLifetime.StopApplication();
    }
}
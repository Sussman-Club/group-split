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

        var seeders = scope.ServiceProvider.GetServices<ISeeder>().ToList();

        logger.LogInformation("Discovered {Count} seeders: {SeederList}",
            seeders.Count,
            string.Join(", ", seeders.Select(s => s.GetType().Name)));

        // Topological sort (layers)
        var layers = seeders.TopologicallySort();

        logger.LogInformation("Topological order determined with {LayerCount} layers.", layers.Count);

        foreach (var (group, depth) in layers.Select((x, i) => (x, i)))
        {
            var batchId = Guid.NewGuid().ToString("N");

            logger.LogInformation(
                "[Batch {BatchId}] Starting seeder batch #{Depth} with {Count} seeders: {Seeders}",
                batchId,
                depth,
                group.Count,
                string.Join(", ", group.Select(s => s.GetType().Name)));

            var batchStart = DateTime.UtcNow;
            int successCount = 0;

            var tasks = group.Select(async seeder =>
            {
                var name = seeder.GetType().Name;
                var start = DateTime.UtcNow;

                try
                {
                    logger.LogInformation(
                        "[Batch {BatchId}] → Starting {Seeder} (Order={Order})",
                        batchId, name, depth);

                    await seeder.SeedAsync(stoppingToken);

                    var duration = DateTime.UtcNow - start;

                    logger.LogInformation(
                        "[Batch {BatchId}] ✓ Seeder {Seeder} finished successfully in {Duration}ms",
                        batchId, name, duration.TotalMilliseconds);

                    Interlocked.Increment(ref successCount);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "[Batch {BatchId}] ⚠ Seeder {Seeder} cancelled",
                        batchId, name);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "[Batch {BatchId}] ✗ Seeder {Seeder} failed after {Elapsed}ms",
                        batchId, name, (DateTime.UtcNow - start).TotalMilliseconds);
                    throw;
                }
            });

            await Task.WhenAll(tasks);

            var batchDuration = DateTime.UtcNow - batchStart;

            logger.LogInformation(
                "[Batch {BatchId}] Completed batch #{Depth}: {Success}/{Total} succeeded in {Duration}ms",
                batchId,
                depth,
                successCount,
                group.Count,
                batchDuration.TotalMilliseconds);
        }

        logger.LogInformation("All database seeding completed successfully.");
        applicationLifetime.StopApplication();
    }
}
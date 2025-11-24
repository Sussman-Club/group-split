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
        foreach (var seeder in seeders.OrderBy(s => s.Order))
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
        }

        logger.LogInformation("Database seeding completed.");
        applicationLifetime.StopApplication();
    }
}
using GroupSplit.Seeder.Abstractions;

namespace GroupSplit.Seeder.Orchestration;

public class SeederRunner : BackgroundService, IAsyncDisposable
{
    private readonly ILogger<SeederRunner> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IList<IAsyncDisposable> _scopes = [];
    private readonly IList<ISeeder> _seeders = [];

    public SeederRunner(ILogger<SeederRunner> logger,
        IHostApplicationLifetime applicationLifetime,
        IServiceScopeFactory scopeFactory,
        IEnumerable<Type> seederTypes)
    {
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        foreach (var seederType in seederTypes)
        {
            var scope = scopeFactory.CreateAsyncScope();
            _scopes.Add(scope);
            if (scope.ServiceProvider.GetRequiredService(seederType) is ISeeder seeder)
                _seeders.Add(seeder);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Discovered {Count} seeders: {SeederList}",
            _seeders.Count,
            string.Join(", ", _seeders.Select(s => s.GetType().Name)));

        var layers = _seeders.TopologicallySort();
        _logger.LogInformation("Topological order determined with {LayerCount} layers.", layers.Count);

        foreach (var (group, depth) in layers.Select((x, i) => (x, i)))
        {
            _logger.LogInformation(
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
                    _logger.LogInformation("Seeder {Seeder} completed", name);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Seeder {Seeder} cancelled", name);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Seeder {Seeder} failed", name);
                    throw;
                }
            });

            await Task.WhenAll(tasks);
            _logger.LogInformation("Batch #{Order} completed.", depth + 1);
        }

        _logger.LogInformation("All database seeding completed.");

        _applicationLifetime.StopApplication();
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        foreach (var asyncDisposable in _scopes)
        {
            await using var scope = asyncDisposable;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }
}
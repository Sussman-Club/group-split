using GroupSplit.Seeder.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Seeder.Orchestration;

public class SeederRunnerBuilder(IServiceCollection services)
{
    private readonly List<Type> _seeders = [];

    public IServiceCollection Services { get; } = services;

    public void AddSeeder<TDatabaseSeeder>() where TDatabaseSeeder : class, ISeeder
    {
        Services.TryAddScoped<TDatabaseSeeder>();
        Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ISeeder, TDatabaseSeeder>(sp => sp.GetRequiredService<TDatabaseSeeder>()));
        _seeders.Add(typeof(TDatabaseSeeder));
    }

    public SeederRunner Build(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<SeederRunner>>();
        var appLifetime = serviceProvider.GetRequiredService<IHostApplicationLifetime>();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        return new SeederRunner(logger, appLifetime, scopeFactory, _seeders);
    }
}
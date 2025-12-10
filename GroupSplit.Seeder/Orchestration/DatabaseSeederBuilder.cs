using GroupSplit.Seeder.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Seeder.Orchestration;

public class DatabaseSeederBuilder(IServiceCollection services)
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

    public DatabaseSeederRunner Build(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<DatabaseSeederRunner>>();
        var appLifetime = serviceProvider.GetRequiredService<IHostApplicationLifetime>();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        return new DatabaseSeederRunner(logger, appLifetime, scopeFactory, _seeders);
    }
}
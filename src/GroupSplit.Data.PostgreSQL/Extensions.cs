using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GroupSplit.Data.PostgreSQL;

public static class Extensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPostgreSqlAppDbContext(
            string? connectionString,
            Action<IServiceProvider, DbContextOptionsBuilder>? optionsAction = null,
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
            ServiceLifetime optionsLifetime = ServiceLifetime.Scoped)
        {
            return services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>((sp, options) =>
            {
                options.UseNpgsql(connectionString, npgsqlOptionsAction);
                optionsAction?.Invoke(sp, options);
            }, contextLifetime, optionsLifetime);
        }

        public IServiceCollection AddPostgreSqlAppDbContextFactory(
            string? connectionString,
            Action<IServiceProvider, DbContextOptionsBuilder>? optionsAction = null,
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null,
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
        {
            services.AddDbContextFactory<PostgreSqlAppDbContext>((sp, options) =>
            {
                options.UseNpgsql(connectionString, npgsqlOptionsAction);
                optionsAction?.Invoke(sp, options);
            }, lifetime);

            services.TryAdd(ServiceDescriptor.Describe(typeof(IDbContextFactory<AppDbContext>), ActivatorUtilities
                    .GetServiceOrCreateInstance<DbContextFactoryAdapter<AppDbContext, PostgreSqlAppDbContext>>,
                lifetime)
            );

            services.TryAdd(ServiceDescriptor.Describe(typeof(AppDbContext),
                sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext(),
                lifetime is ServiceLifetime.Transient ? ServiceLifetime.Transient : ServiceLifetime.Scoped));

            return services;
        }
    }

    private class DbContextFactoryAdapter<TContext, TImplementation>(IDbContextFactory<TImplementation> inner)
        : IDbContextFactory<TContext>
        where TContext : DbContext
        where TImplementation : TContext
    {
        public TContext CreateDbContext() => inner.CreateDbContext();

        public async Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken)
            => await inner.CreateDbContextAsync(cancellationToken);
    }
}
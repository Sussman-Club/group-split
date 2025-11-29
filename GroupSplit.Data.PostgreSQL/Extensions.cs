using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GroupSplit.Data.PostgreSQL;

public static class Extensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPostgreSqlAppDbContext(
            string? connectionString,
            Action<DbContextOptionsBuilder>? optionsAction = null,
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
            ServiceLifetime optionsLifetime = ServiceLifetime.Scoped)
        {
            return services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsqlOptionsAction);
                optionsAction?.Invoke(options);
            }, contextLifetime, optionsLifetime);
        }
    }
}
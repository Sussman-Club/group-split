using Aspire.Npgsql.EntityFrameworkCore.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Data.PostgreSQL;

public static class Extensions
{
    extension(IHostApplicationBuilder appBuilder)
    {
        public IHostApplicationBuilder AddPostgreSqlAppDbContext(
            string connectionName,
            Action<NpgsqlEntityFrameworkCorePostgreSQLSettings>? configureSettings = null,
            Action<DbContextOptionsBuilder>? configureDbContextOptions = null)
        {
            appBuilder.AddNpgsqlDbContext<PostgreSqlAppDbContext>(connectionName, configureSettings,
                configureDbContextOptions);
            
            appBuilder.Services.TryAddScoped<AppDbContext, PostgreSqlAppDbContext>();

            return appBuilder;
        }
    }
}
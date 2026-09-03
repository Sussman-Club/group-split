using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.Data.PostgreSQL.Migrations;

// Advertises both context types on purpose. "dotnet ef --context AppDbContext" drives local
// migrations, but the migrations themselves are [DbContext(typeof(PostgreSqlAppDbContext))], and a
// published efbundle resolves the context itself with no way to override it, landing on the derived
// type. Without the second interface that bundle finds no factory and fails at startup.
public class AppContextPostgreSqlFactory
    : IDesignTimeDbContextFactory<AppDbContext>, IDesignTimeDbContextFactory<PostgreSqlAppDbContext>
{
    AppDbContext IDesignTimeDbContextFactory<AppDbContext>.CreateDbContext(string[] args)
        => CreateDbContext(args);

    public PostgreSqlAppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationManager();

        config.AddUserSecrets<AppContextPostgreSqlFactory>();
        config.AddEnvironmentVariables("DOTNET_");
        config.AddEnvironmentVariables();
        config.AddCommandLine(args);
        
        var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlAppDbContext>();
        
        var connectionString = config.GetConnectionString("DefaultConnection") 
                               ?? "Host=localhost;Port=5432;Database=my_db;Username=my_user;Password=my_password";
        
        optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly(Assembly.GetAssembly(typeof(AppContextPostgreSqlFactory))!));
        
        return new PostgreSqlAppDbContext(optionsBuilder.Options);
    }
}
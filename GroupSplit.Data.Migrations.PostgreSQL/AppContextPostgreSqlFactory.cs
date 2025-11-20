using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.Data.Migrations.PostgreSQL;

public class AppContextPostgreSqlFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationManager();

        config.AddUserSecrets<AppContextPostgreSqlFactory>();
        config.AddEnvironmentVariables("DOTNET_");
        config.AddEnvironmentVariables();
        config.AddCommandLine(args);
        
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        var connectionString = config.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=my_db;Username=my_user;Password=my_password";
        
        optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly(Assembly.GetAssembly(typeof(AppContextPostgreSqlFactory))!));
        
        return new AppDbContext(optionsBuilder.Options);
    }
}
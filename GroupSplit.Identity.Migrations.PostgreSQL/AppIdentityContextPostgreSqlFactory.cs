using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.Identity.Migrations.PostgreSQL;

public class AppIdentityContextPostgreSqlFactory : IDesignTimeDbContextFactory<AppIdentityContext>
{
    public AppIdentityContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationManager();

        config.AddUserSecrets<AppIdentityContextPostgreSqlFactory>();
        config.AddEnvironmentVariables("DOTNET_");
        config.AddEnvironmentVariables();
        config.AddCommandLine(args);
        
        var optionsBuilder = new DbContextOptionsBuilder<AppIdentityContext>();

        var connectionString = config.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=my_db;Username=my_user;Password=my_password";
        
        optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly(Assembly.GetAssembly(typeof(AppIdentityContextPostgreSqlFactory))!));
        
        return new AppIdentityContext(optionsBuilder.Options);
    }
}
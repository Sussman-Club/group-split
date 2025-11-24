using GroupSplit.Data;
using GroupSplit.Identity;
using GroupSplit.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services.AddDbContext<AppIdentityContext>(options => options.UseNpgsql(config.GetConnectionString("identity")));
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(config.GetConnectionString("db")));

        services.AddDatabaseSeeders();
        services.AddScoped<DatabaseSeederRunner>();
    })
    .Build();

var databaseSeederRunner = host.Services.GetRequiredService<DatabaseSeederRunner>();
await databaseSeederRunner.RunAsync();
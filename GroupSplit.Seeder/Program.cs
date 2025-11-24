using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Identity;
using GroupSplit.Seeder;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services.AddDbContext<AppIdentityContext>(options => options.UseNpgsql(config.GetConnectionString("identity")));
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(config.GetConnectionString("db")));

        services.AddScoped<IDatabaseSeeder>(sp =>
            new JsonSeeder<Group, AppDbContext>(
                sp.GetRequiredService<AppDbContext>(),
                GetFilePath("groups.json"),
                sp.GetRequiredService<ILogger<JsonSeeder<Group, AppDbContext>>>()
            )
        );

        services.AddScoped<IDatabaseSeeder>(sp =>
            new UserSeeder(
                sp.GetRequiredService<AppDbContext>(),
                GetFilePath("users.json"),
                sp.GetRequiredService<ILogger<UserSeeder>>()
            )
        );

        services.AddScoped<DatabaseSeederRunner>();
    })
    .Build();

var databaseSeederRunner = host.Services.GetRequiredService<DatabaseSeederRunner>();
await databaseSeederRunner.RunAsync();

string GetFilePath(string fileName)
{
    var relativePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SeedData", fileName);
    return Path.GetFullPath(relativePath);
}
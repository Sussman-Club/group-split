using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Seeder;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDatabaseSeeders()
        {
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

            return services;

            string GetFilePath(string fileName)
            {
                var relativePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SeedData", fileName);
                return Path.GetFullPath(relativePath);
            }
        }
    }
}
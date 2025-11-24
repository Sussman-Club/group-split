using GroupSplit.Identity;
using GroupSplit.Seeder.Dtos;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder;

public static class SeederServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSeedDataSources()
        {
            services.AddSeedSource<GroupSeedDto>(opt => opt.Paths.Groups);
            services.AddSeedSource<UserSeedDto>(opt => opt.Paths.Users);
            services.AddSeedSource<IdentityUserSeedDto>(opt => opt.Paths.IdentityUsers);
            return services;
        }

        public IServiceCollection AddDatabaseSeeders()
        {
            services.AddSeeder<GroupSeeder>();
            services.AddSeeder<UserSeeder>();
            return services;
        }

        public IServiceCollection AddIdentityDatabaseSeeder()
        {
            services.AddIdentityCore<User>().AddEntityFrameworkStores<AppIdentityContext>();
            services.AddSeeder<IdentityUserSeeder>();
            return services;
        }

        private IServiceCollection AddSeeder<TDatabaseSeeder>() where TDatabaseSeeder : class, ISeeder
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISeeder, TDatabaseSeeder>());
            return services;
        }

        private void AddSeedSource<T>(Func<SeederOptions, string> pathSelector)
        {
            services.AddSingleton<ISeedDataSource<T>>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<SeederOptions>>().Value;
                var logger = sp.GetRequiredService<ILogger<JsonArrayFileDataSource<T>>>();

                var path = pathSelector(options);

                return new JsonArrayFileDataSource<T>(path, logger);
            });
        }
    }
}
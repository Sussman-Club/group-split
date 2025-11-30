using GroupSplit.Identity;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.DataSources;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder;

public static class ServiceCollectionSeederExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSeedDataSources()
        {
            services.AddJsonSeedSource<GroupSeedDto>(opt => opt.Paths.Groups);
            services.AddJsonSeedSource<UserSeedDto>(opt => opt.Paths.Users);
            services.AddJsonSeedSource<RuleSeedDto>(opt => opt.Paths.Rules);
            services.AddJsonSeedSource<RuleVersionSeedDto>(opt => opt.Paths.RuleVersions);
            services.AddJsonSeedSource<TransactionSeedDto>(opt => opt.Paths.Transactions);
            services.AddJsonSeedSource<IdentityUserSeedDto>(opt => opt.Paths.IdentityUsers);
            return services;
        }

        public IServiceCollection AddSeeders()
        {
            // Identity seeders
            services.AddIdentityCore<User>().AddEntityFrameworkStores<AppIdentityContext>();
            services.AddSeeder<IdentityUserSeeder>();

            // App seeders
            services.AddSeeder<GroupSeeder>();
            services.AddSeeder<UserSeeder>();
            services.AddSeeder<RuleSeeder>();
            services.AddSeeder<RuleVersionSeeder>();
            services.AddSeeder<TransactionSeeder>();

            return services;
        }

        private IServiceCollection AddSeeder<TDatabaseSeeder>() where TDatabaseSeeder : class, ISeeder
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISeeder, TDatabaseSeeder>());
            return services;
        }

        private void AddJsonSeedSource<TDto>(Func<SeederOptions, string> pathSelector)
        {
            services.AddSingleton<ISeedDataSource<TDto>>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<SeederOptions>>().Value;
                var logger = sp.GetRequiredService<ILogger<JsonArrayFileDataSource<TDto>>>();

                var path = pathSelector(options);

                return new JsonArrayFileDataSource<TDto>(path, logger);
            });
        }
    }
}
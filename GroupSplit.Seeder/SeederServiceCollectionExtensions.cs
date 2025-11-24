using GroupSplit.Identity;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Seeder;

public static class SeederServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
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

        private IServiceCollection AddSeeder<TDatabaseSeeder>() where TDatabaseSeeder : class, IDatabaseSeeder
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IDatabaseSeeder, TDatabaseSeeder>());
            return services;
        }
    }
}
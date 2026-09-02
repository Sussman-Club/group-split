using GroupSplit.API.Extensions;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.DataSources;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Orchestration;

public static class Extensions
{
    extension(IServiceCollection services)
    {
        public SeederRunnerBuilder AddSeederRunner()
        {
            var seederBuilder = new SeederRunnerBuilder(services);

            services.AddHostedService<SeederRunner>(sp => seederBuilder.Build(sp));

            return seederBuilder;
        }
    }

    extension(SeederRunnerBuilder builder)
    {
        public SeederRunnerBuilder AddSeeders()
        {
            // Data sources
            builder.Services.AddSeedDataSources();

            // App seeders
            builder.AddSeeder<GroupSeeder>();
            builder.AddSeeder<UserSeeder>();
            builder.Services.AddRuleVersionServices();
            builder.AddSeeder<RuleSeeder>();
            builder.AddSeeder<TransactionSeeder>();

            return builder;
        }
    }

    extension(IServiceCollection services)
    {
        private IServiceCollection AddSeedDataSources()
        {
            services.AddJsonSeedSource<GroupSeedDto>(opt => opt.Paths.Groups);
            services.AddJsonSeedSource<UserSeedDto>(opt => opt.Paths.Users);
            services.AddJsonSeedSource<RuleSeedDto>(opt => opt.Paths.Rules);
            services.AddJsonSeedSource<TransactionSeedDto>(opt => opt.Paths.Transactions);
            services.AddJsonSeedSource<IdentityUserSeedDto>(opt => opt.Paths.IdentityUsers);
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
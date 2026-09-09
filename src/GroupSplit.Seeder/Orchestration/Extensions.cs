using GroupSplit.API.Services;
﻿using GroupSplit.API.Extensions;
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
            builder.Services.AddSplitRuleServices();

            // The transaction seeder divides through the same code the API does, so a
            // developer's seeded balances are ones the app could have produced. Scoped,
            // like the API registers it, because it writes through the DbContext.
            builder.Services.AddScoped<IExpenseSplitter, ExpenseSplitter>();
            builder.AddSeeder<CategorySeeder>();
            builder.AddSeeder<TransactionSeeder>();

            // Repayments, after the expenses. Without them every seeded balance only ever
            // grows: the Settle page has no history and a ledger filtered to settlements
            // is empty.
            builder.AddSeeder<SettlementSeeder>();

            // Demo bank data. Its provider has no connector, so nothing ever tries to
            // sync it; it exists so the inbox is not empty for anybody without Plaid.
            builder.AddSeeder<BankConnectionSeeder>();

            // Identity provider. Reads the same users.json as UserSeeder but writes to
            // Keycloak rather than the database, so it depends on none of the above.
            builder.AddSeeder<KeycloakUserSeeder>();

            return builder;
        }
    }

    extension(IServiceCollection services)
    {
        private IServiceCollection AddSeedDataSources()
        {
            services.AddJsonSeedSource<GroupSeedDto>(opt => opt.Paths.Groups);
            services.AddJsonSeedSource<UserSeedDto>(opt => opt.Paths.Users);
            services.AddJsonSeedSource<CategorySeedDto>(opt => opt.Paths.Categories);
            services.AddJsonSeedSource<TransactionSeedDto>(opt => opt.Paths.Transactions);
            services.AddJsonSeedSource<SettlementSeedDto>(opt => opt.Paths.Settlements);
            services.AddJsonSeedSource<BankConnectionSeedDto>(opt => opt.Paths.BankConnections);
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

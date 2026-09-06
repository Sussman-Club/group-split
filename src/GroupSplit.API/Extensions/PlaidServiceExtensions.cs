using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Plaid;
using Going.Plaid;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Registers Plaid as a bank connector, when this deployment has been given credentials
/// for it.
/// </summary>
/// <remarks>
/// Whether it registers <em>is</em> the switch. There is no separate enabled flag: bank
/// sync is available exactly when a connector answers for the configured provider, so
/// "configured" and "on" cannot disagree, and a deployment with no Plaid credentials starts
/// perfectly well with the bank features saying so.
/// <para>
/// Credentials come from the <c>Plaid</c> configuration section that Going.Plaid binds:
/// <c>ClientId</c>, <c>Secret</c> and <c>Environment</c>. Locally they come from user
/// secrets; in a deployment they arrive as <c>Plaid__*</c> environment variables from the
/// Aspire parameters.
/// </para>
/// </remarks>
public static class PlaidServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPlaidConnector(IConfiguration configuration)
        {
            var section = configuration.GetSection(PlaidConnectorOptions.SectionName);

            if (string.IsNullOrWhiteSpace(section["ClientId"]) || string.IsNullOrWhiteSpace(section["Secret"]))
                return services;

            services.AddPlaid(configuration);
            services.AddOptions<PlaidConnectorOptions>().Bind(section);

            services.AddSingleton<PlaidWebhookVerifier>();
            services.AddKeyedScoped<IBankConnector, PlaidConnector>(PlaidConnector.Name);

            return services;
        }
    }
}

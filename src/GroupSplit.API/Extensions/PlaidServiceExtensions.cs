using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Plaid;
using Going.Plaid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

            // The section, never the root. Going.Plaid has two overloads: one takes a
            // configuration root and finds its own section, the other binds whatever it is
            // handed, as-is. Handing the root to the second one binds Plaid's options
            // against top-level keys -- so the credentials come back empty, and `Environment`
            // binds to ASP.NET Core's own `environment` key, which locally reads
            // "Development" and points the client at a Plaid host that no longer exists.
            services.AddPlaid(section);

            services.AddOptions<PlaidConnectorOptions>()
                .Bind(section)
                .ValidateOnStart();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<PlaidConnectorOptions>, PlaidConnectorOptionsValidator>());

            // Everything this needs, registered here. The clock is also registered by
            // AddBankingServices, so relying on that would work today and break the moment
            // a host called these in the other order -- which is exactly the kind of
            // ordering dependency that only shows up once something is resolved.
            services.AddLogging();
            services.TryAddSingleton(TimeProvider.System);

            services.AddSingleton<PlaidWebhookVerifier>();
            services.AddKeyedScoped<IBankConnector, PlaidConnector>(PlaidConnector.Name);

            return services;
        }
    }
}

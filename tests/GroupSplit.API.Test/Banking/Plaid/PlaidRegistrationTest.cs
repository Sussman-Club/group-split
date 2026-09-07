using GroupSplit.API.Extensions;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Plaid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlaidOptions = Going.Plaid.PlaidOptions;

namespace GroupSplit.API.Test.Banking.Plaid;

/// <summary>
/// What the API is registered with when a deployment hands it configuration.
/// </summary>
/// <remarks>
/// This exists because the registration got it wrong in a way nothing else could catch.
/// Going.Plaid has two <c>AddPlaid</c> overloads: one takes a configuration root and finds
/// its own section, the other binds whatever it is handed. The root went to the second one,
/// so Plaid's options bound against top-level keys -- the credentials came back empty, and
/// <c>Environment</c> bound to ASP.NET Core's own <c>environment</c> key, which reads
/// "Development" on a developer's machine and pointed the client at a Plaid host that no
/// longer exists.
/// <para>
/// Nothing failed at startup, and the unit tests all passed, because they construct the
/// connector with a client of their own. It surfaced as a DNS error in a running app. So
/// what is asserted here is the wiring itself.
/// </para>
/// </remarks>
public class PlaidRegistrationTest
{
    /// <summary>
    /// A root configuration shaped like the one a running API has: the Plaid section from
    /// the AppHost's parameters, and the host's own <c>environment</c> key beside it.
    /// </summary>
    private static IConfiguration RootConfiguration(string? plaidEnvironment = "Sandbox") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // What ASP.NET Core puts at the root from ASPNETCORE_ENVIRONMENT. It is the
                // key that quietly captured Plaid's own Environment.
                ["environment"] = "Development",

                ["Plaid:ClientId"] = "client-from-the-parameters",
                ["Plaid:Secret"] = "secret-from-the-parameters",
                ["Plaid:Environment"] = plaidEnvironment
            })
            .Build();

    [Fact]
    public void Plaid_is_configured_from_its_own_section_and_not_from_the_root()
    {
        var services = new ServiceCollection().AddPlaidConnector(RootConfiguration());

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<PlaidOptions>>().Value;

        Assert.Equal("client-from-the-parameters", options.ClientId);
        Assert.Equal("secret-from-the-parameters", options.Secret);

        // Not Development, which is what the root's own key says and what the client would
        // have used to build a hostname Plaid retired.
        Assert.Equal(Going.Plaid.Environment.Sandbox, options.Environment);
    }

    [Fact]
    public void A_deployment_that_asks_for_production_gets_production()
    {
        var services = new ServiceCollection().AddPlaidConnector(RootConfiguration("Production"));

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<PlaidOptions>>().Value;

        Assert.Equal(Going.Plaid.Environment.Production, options.Environment);
    }

    [Fact]
    public void A_connector_answers_for_plaid_once_it_is_configured()
    {
        var services = new ServiceCollection().AddPlaidConnector(RootConfiguration());

        var connector = services.BuildServiceProvider().GetKeyedService<IBankConnector>(PlaidConnector.Name);

        Assert.NotNull(connector);
        Assert.Equal(PlaidConnector.Name, connector.Provider);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("client", null)]
    [InlineData(null, null)]
    public void Without_credentials_nothing_is_registered_and_bank_sync_reads_as_off(string? clientId, string? secret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plaid:ClientId"] = clientId,
                ["Plaid:Secret"] = secret
            })
            .Build();

        var services = new ServiceCollection().AddPlaidConnector(configuration);

        // Whether a connector answers is what "bank sync is available" means, so an API with
        // no credentials starts and says so rather than offering a button that cannot work.
        Assert.Null(services.BuildServiceProvider().GetKeyedService<IBankConnector>(PlaidConnector.Name));
    }
}

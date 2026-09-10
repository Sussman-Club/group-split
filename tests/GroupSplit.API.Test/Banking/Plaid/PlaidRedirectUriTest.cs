using GroupSplit.API.Extensions;
using GroupSplit.API.Services.Banking.Plaid;
using GroupSplit.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Test.Banking.Plaid;

/// <summary>
/// The redirect address, which is the one value in the linking flow that three parties have
/// to agree on in advance: Plaid's dashboard, the link token, and the page the web app
/// serves.
/// </summary>
/// <remarks>
/// Nothing can reconcile them at runtime — a provider is told the address up front and
/// refuses to open a session against one it does not recognise. So a wrong value is not a
/// degraded flow; it is every OAuth bank failing to link, found by the first person who
/// tries one. These pin that it is found by the deploy instead.
/// </remarks>
public class PlaidRedirectUriTest
{
    private const string Good = "https://groupsplit.example.com" + BankLinkAddresses.OAuthReturnPath;

    [Fact]
    public void The_address_the_web_app_serves_is_accepted()
    {
        Assert.Null(Failure(Good));
    }

    /// <summary>
    /// The ordinary configuration, and the one every deployment runs today: Plaid Link does
    /// OAuth in a popup and never leaves the page, so there is no address to agree on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_address_is_the_popup_flow_and_is_fine(string? configured)
    {
        Assert.Null(Failure(configured));
    }

    /// <summary>
    /// A deployment serving the app under a prefix still ends at the same page, so the
    /// check is on the end of the path rather than the whole of it.
    /// </summary>
    [Fact]
    public void An_app_served_under_a_prefix_is_accepted()
    {
        Assert.Null(Failure("https://example.com/groupsplit" + BankLinkAddresses.OAuthReturnPath));
    }

    [Fact]
    public void An_address_this_application_does_not_serve_is_refused_by_name()
    {
        var failure = Failure("https://groupsplit.example.com/plaid-return");

        Assert.NotNull(failure);
        Assert.Contains("/plaid-return", failure);
        Assert.Contains(BankLinkAddresses.OAuthReturnPath, failure);
        Assert.Contains("Plaid dashboard", failure);
    }

    [Fact]
    public void An_address_that_is_not_https_is_refused()
    {
        var failure = Failure("http://groupsplit.example.com" + BankLinkAddresses.OAuthReturnPath);

        Assert.NotNull(failure);
        Assert.Contains("https", failure);
    }

    [Fact]
    public void Something_that_is_not_a_url_is_refused()
    {
        Assert.Contains("absolute URL", Failure("groupsplit.example.com/bank/oauth")!);
    }

    [Theory]
    [InlineData("?state=1")]
    [InlineData("#done")]
    public void A_query_or_fragment_is_refused_because_plaid_matches_exactly(string tail)
    {
        Assert.Contains("query or fragment", Failure(Good + tail)!);
    }

    /// <summary>
    /// Bound and validated on start, so the deploy fails rather than the first link. This
    /// resolves the options the way the host does rather than calling the validator
    /// directly, which is the half that would otherwise go untested: a validator nothing
    /// registered is a validator that never runs.
    /// </summary>
    [Fact]
    public void The_validator_is_wired_into_the_options_the_connector_reads()
    {
        var services = new ServiceCollection();

        services.AddPlaidConnector(Configuration("https://groupsplit.example.com/somewhere-else"));

        var e = Assert.Throws<OptionsValidationException>(
            () => services.BuildServiceProvider().GetRequiredService<IOptions<PlaidConnectorOptions>>().Value);

        Assert.Contains(BankLinkAddresses.OAuthReturnPath, e.Message);
    }

    private static string? Failure(string? redirectUri)
    {
        var result = new PlaidConnectorOptionsValidator()
            .Validate(null, new PlaidConnectorOptions { RedirectUri = redirectUri });

        return result.Failed ? result.FailureMessage : null;
    }

    private static IConfiguration Configuration(string redirectUri) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plaid:ClientId"] = "a-client-id",
                ["Plaid:Secret"] = "a-secret",
                ["Plaid:RedirectUri"] = redirectUri
            })
            .Build();
}

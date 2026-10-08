using System.Text.Json.Nodes;
using GroupSplit.Keycloak;

namespace GroupSplit.Seeder.Test.Realm;

/// <summary>
/// What the checked-in realm allows, read from the same embedded file Keycloak imports and
/// the realm worker reconciles from.
/// </summary>
public sealed class RealmDefinitionTests
{
    /// <summary>
    /// A wildcard redirect URI on a public client hands an authorization code to whoever
    /// builds the link -- PKCE does not help when the attacker chose the challenge. Every
    /// client, not only <c>web-app</c>, so a new one cannot bring it back.
    /// </summary>
    [Fact]
    public void No_client_accepts_a_wildcard_redirect_or_origin()
    {
        foreach (var client in Clients())
        {
            var clientId = client["clientId"]!.GetValue<string>();

            foreach (var name in new[] { "redirectUris", "webOrigins" })
            {
                foreach (var value in Strings(client[name]))
                {
                    Assert.False(value.Contains('*'), $"Client '{clientId}' allows '{value}' in {name}.");
                }
            }

            if (client["attributes"]?["post.logout.redirect.uris"]?.GetValue<string>() is { } postLogout)
            {
                Assert.DoesNotContain("*", postLogout);
                Assert.NotEqual("+", postLogout);
            }
        }
    }

    /// <summary>
    /// The two paths the OpenID Connect handler actually uses, on the one origin the web app
    /// is served from, and no browser origin at all: the web app is the only thing that talks
    /// to Keycloak's endpoints, and it does so from the server.
    /// </summary>
    [Fact]
    public void The_web_client_redirects_only_to_its_own_callbacks()
    {
        var client = KeycloakRealmDefinition.Client(KeycloakRealmDefinition.Load(), "web-app")!;

        Assert.Equal(["${WEB_HOSTNAME}/signin-oidc"], Strings(client["redirectUris"]));
        Assert.Empty(Strings(client["webOrigins"]));
        Assert.Equal("${WEB_HOSTNAME}/signout-callback-oidc",
            client["attributes"]!["post.logout.redirect.uris"]!.GetValue<string>());
    }

    /// <summary>What the file restricts, the worker has to be able to put back on a realm that already exists.</summary>
    [Fact]
    public void The_redirect_settings_are_reconciled()
    {
        var managed = Assert.Single(KeycloakRealmDefinition.ManagedClients, client => client.ClientId == "web-app");

        Assert.Contains("redirectUris", managed.Properties);
        Assert.Contains("webOrigins", managed.Properties);
        Assert.Contains("post.logout.redirect.uris", managed.Attributes);
    }

    /// <summary>
    /// An address Google has not verified is one somebody typed into Google. Keycloak's
    /// essential-claim filter refuses the sign-in before any account is looked up, and it
    /// compares the ID token's claim as text against a pattern -- so the value is the string
    /// <c>true</c>, matched whole.
    /// </summary>
    [Fact]
    public void Google_links_only_on_an_address_google_verified()
    {
        var config = KeycloakRealmDefinition.IdentityProvider(KeycloakRealmDefinition.Load(), "google")!["config"]!;

        Assert.Equal("true", config["filteredByClaim"]!.GetValue<string>());
        Assert.Equal("email_verified", config["claimFilterName"]!.GetValue<string>());
        Assert.Equal("true", config["claimFilterValue"]!.GetValue<string>());
    }

    /// <summary>
    /// The lookup runs on the brokered email, and anything that ran before it and showed a
    /// form -- a profile review, above all -- would let the person signing in replace that
    /// email with someone else's. Detect then link is all the flow may do.
    /// </summary>
    [Fact]
    public void Every_first_broker_login_goes_straight_from_the_providers_address_to_the_link()
    {
        var definition = KeycloakRealmDefinition.Load();

        foreach (var provider in Providers())
        {
            var flow = KeycloakRealmDefinition.Flow(definition, provider["firstBrokerLoginFlowAlias"]!.GetValue<string>())!;

            Assert.Equal(
                [
                    new FlowStep("idp-detect-existing-broker-user", "REQUIRED"),
                    new FlowStep("idp-auto-link", "REQUIRED")
                ],
                KeycloakRealmDefinition.Steps(flow));
        }
    }

    /// <summary>
    /// A provider the worker does not reconcile keeps whatever the realm was first imported
    /// with, which is how the profile form outlived the file that removed it.
    /// </summary>
    [Fact]
    public void Every_identity_provider_is_reconciled()
    {
        var managed = KeycloakRealmDefinition.ManagedIdentityProviders.Select(provider => provider.Alias).ToArray();

        foreach (var provider in Providers())
        {
            Assert.Contains(provider["alias"]!.GetValue<string>(), managed);
        }

        var google = Assert.Single(KeycloakRealmDefinition.ManagedIdentityProviders, provider => provider.Alias == "google");
        Assert.Equal(["trustEmail", "firstBrokerLoginFlowAlias"], google.Properties);
        Assert.Equal(["filteredByClaim", "claimFilterName", "claimFilterValue"], google.Config);
    }

    private static IEnumerable<JsonObject> Providers() =>
        KeycloakRealmDefinition.Load()["identityProviders"]!.AsArray().OfType<JsonObject>();

    private static IEnumerable<JsonObject> Clients() =>
        KeycloakRealmDefinition.Load()["clients"]!.AsArray().OfType<JsonObject>();

    private static string[] Strings(JsonNode? node) =>
        node?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
}

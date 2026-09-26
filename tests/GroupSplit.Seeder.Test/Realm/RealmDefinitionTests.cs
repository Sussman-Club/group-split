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

    private static IEnumerable<JsonObject> Clients() =>
        KeycloakRealmDefinition.Load()["clients"]!.AsArray().OfType<JsonObject>();

    private static string[] Strings(JsonNode? node) =>
        node?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
}

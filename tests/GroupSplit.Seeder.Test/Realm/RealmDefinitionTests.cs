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

    /// <summary>
    /// Which flow runs each kind of sign-in is stated, not left to Keycloak's defaults, and
    /// reconciled, so a binding changed on an existing realm is changed back.
    /// </summary>
    [Theory]
    [InlineData("browserFlow", "browser")]
    [InlineData("registrationFlow", "registration")]
    [InlineData("directGrantFlow", "direct grant")]
    [InlineData("resetCredentialsFlow", "reset credentials")]
    [InlineData("clientAuthenticationFlow", "clients")]
    [InlineData("firstBrokerLoginFlow", "first broker login")]
    public void Every_flow_binding_is_named_and_reconciled(string binding, string flow)
    {
        Assert.Equal(flow, KeycloakRealmDefinition.Load()[binding]!.GetValue<string>());
        Assert.Contains(binding, KeycloakRealmDefinition.ManagedSettings);
    }

    /// <summary>
    /// The device grant and nothing else: a terminal has no redirect URI to give, and a
    /// password grant would have the CLI handle the password itself.
    /// </summary>
    [Fact]
    public void The_cli_client_accepts_only_the_device_grant_and_is_reconciled()
    {
        var cli = KeycloakRealmDefinition.Client(KeycloakRealmDefinition.Load(), "cli")!;

        Assert.True(cli["publicClient"]!.GetValue<bool>());
        Assert.False(cli["standardFlowEnabled"]!.GetValue<bool>());
        Assert.False(cli["implicitFlowEnabled"]!.GetValue<bool>());
        Assert.False(cli["directAccessGrantsEnabled"]!.GetValue<bool>());
        Assert.False(cli["serviceAccountsEnabled"]!.GetValue<bool>());
        Assert.Equal("true", cli["attributes"]!["oauth2.device.authorization.grant.enabled"]!.GetValue<string>());
        Assert.Equal("false", cli["attributes"]!["oidc.ciba.grant.enabled"]!.GetValue<string>());
        Assert.Equal("api", KeycloakRealmDefinition.ProtocolMapper(cli, "audience-mapper")!
            ["config"]!["included.client.audience"]!.GetValue<string>());

        var managed = Assert.Single(KeycloakRealmDefinition.ManagedClients, client => client.ClientId == "cli");
        Assert.True(managed.CreateWhenMissing);
        Assert.Equal(["audience-mapper"], managed.ProtocolMappers);
        Assert.Equal(
            ["publicClient", "standardFlowEnabled", "implicitFlowEnabled", "directAccessGrantsEnabled", "serviceAccountsEnabled"],
            managed.Properties);
        Assert.Equal(["oauth2.device.authorization.grant.enabled", "oidc.ciba.grant.enabled"], managed.Attributes);
    }

    /// <summary>
    /// A client the worker may create is created from the file as it stands, placeholders and
    /// all; one that depended on the environment would be created wrong somewhere.
    /// </summary>
    [Fact]
    public void A_client_the_worker_creates_depends_on_nothing_in_the_environment()
    {
        var definition = KeycloakRealmDefinition.Load();

        foreach (var managed in KeycloakRealmDefinition.ManagedClients.Where(client => client.CreateWhenMissing))
        {
            Assert.DoesNotContain("${", KeycloakRealmDefinition.Client(definition, managed.ClientId)!.ToJsonString());
        }
    }

    /// <summary>
    /// The client the API deletes logins through holds user management in the app's realm.
    /// It must sign in as itself and in no other way: a browser flow or a password grant on it
    /// would be a user-management login anybody could try.
    /// </summary>
    [Fact]
    public void The_account_deletion_client_can_only_sign_in_as_itself()
    {
        var client = KeycloakRealmDefinition.Client(KeycloakRealmDefinition.Load(), "account-deletion")!;

        Assert.False(client["publicClient"]!.GetValue<bool>());
        Assert.True(client["serviceAccountsEnabled"]!.GetValue<bool>());
        Assert.False(client["standardFlowEnabled"]!.GetValue<bool>());
        Assert.False(client["implicitFlowEnabled"]!.GetValue<bool>());
        Assert.False(client["directAccessGrantsEnabled"]!.GetValue<bool>());
        Assert.Empty(Strings(client["redirectUris"]));

        // Never a literal: the deployment supplies it, and the worker holds the client to it.
        Assert.Equal("${KEYCLOAK_ACCOUNT_DELETION_SECRET}", client["secret"]!.GetValue<string>());
    }

    /// <summary>
    /// Deleting a user and ending their sessions are both <c>manage-users</c>, and that is all
    /// the client is for. Nothing in the realm, nothing in another client, no realm role.
    /// </summary>
    [Fact]
    public void The_account_deletion_service_account_can_manage_users_and_nothing_else()
    {
        var definition = KeycloakRealmDefinition.Load();

        Assert.Equal(["manage-users"], KeycloakRealmDefinition.ServiceAccountRoles(definition, "account-deletion"));

        var account = Assert.Single(definition["users"]!.AsArray().OfType<JsonObject>(),
            user => user["serviceAccountClientId"]?.GetValue<string>() == "account-deletion");

        Assert.Null(account["realmRoles"]);
        Assert.Equal(["realm-management"], account["clientRoles"]!.AsObject().Select(entry => entry.Key));
    }

    /// <summary>The import never runs again on a realm that exists, so the worker has to own it.</summary>
    [Fact]
    public void The_account_deletion_client_is_owned_by_the_worker()
    {
        var service = Assert.Single(KeycloakRealmDefinition.ServiceClients);

        Assert.Equal("account-deletion", service.ClientId);
        Assert.Equal("KEYCLOAK_ACCOUNT_DELETION_SECRET", service.SecretVariable);
    }

    /// <summary>
    /// Who signed in or failed to, who linked or unlinked a Google account, who reset a
    /// password, and which admin changed what -- kept for ninety days and reconciled, so a
    /// realm imported before the file said so records them too.
    /// </summary>
    [Fact]
    public void The_realm_records_sign_in_and_admin_events()
    {
        var realm = KeycloakRealmDefinition.Load();

        Assert.True(realm["eventsEnabled"]!.GetValue<bool>());
        Assert.Equal(TimeSpan.FromDays(90).TotalSeconds, realm["eventsExpiration"]!.GetValue<long>());
        Assert.Equal(["jboss-logging"], Strings(realm["eventsListeners"]));
        Assert.True(realm["adminEventsEnabled"]!.GetValue<bool>());

        var types = Strings(realm["enabledEventTypes"]);

        foreach (var type in new[]
                 {
                     "LOGIN", "LOGIN_ERROR", "IDENTITY_PROVIDER_FIRST_LOGIN", "IDENTITY_PROVIDER_LINK_ACCOUNT",
                     "FEDERATED_IDENTITY_LINK", "REMOVE_FEDERATED_IDENTITY", "UPDATE_PASSWORD", "RESET_PASSWORD",
                     "UPDATE_EMAIL", "SEND_RESET_PASSWORD", "UPDATE_PROFILE", "DELETE_ACCOUNT"
                 })
        {
            Assert.Contains(type, types);
        }

        foreach (var name in new[]
                 {
                     "eventsEnabled", "eventsExpiration", "eventsListeners", "enabledEventTypes",
                     "adminEventsEnabled", "adminEventsDetailsEnabled"
                 })
        {
            Assert.Contains(name, KeycloakRealmDefinition.ManagedSettings);
        }
    }

    /// <summary>
    /// An admin event's details are the representation that was sent, which can carry a client
    /// secret or a user's initial password, and the event store keeps it for ninety days.
    /// </summary>
    [Fact]
    public void Admin_events_leave_out_the_representation_sent()
    {
        Assert.False(KeycloakRealmDefinition.Load()["adminEventsDetailsEnabled"]!.GetValue<bool>());
    }

    private static IEnumerable<JsonObject> Providers() =>
        KeycloakRealmDefinition.Load()["identityProviders"]!.AsArray().OfType<JsonObject>();

    private static IEnumerable<JsonObject> Clients() =>
        KeycloakRealmDefinition.Load()["clients"]!.AsArray().OfType<JsonObject>();

    private static string[] Strings(JsonNode? node) =>
        node?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
}

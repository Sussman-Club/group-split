using System.Text.Json.Nodes;
using GroupSplit.Keycloak;
using GroupSplit.RealmWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Realm;

public sealed class RealmSettingsReconcilerTests
{
    private const string Origin = "https://groupsplit.example";

    private static string? Environment(string name) =>
        name == "WEB_HOSTNAME" ? Origin : null;

    /// <summary>
    /// The realm as a first-boot import with <see cref="Origin"/> would have written it. The
    /// container gets the SMTP and Google values as well, which the worker does not.
    /// </summary>
    private static JsonObject Imported() =>
        (JsonObject)KeycloakPlaceholders.Expand(KeycloakRealmDefinition.Load(),
            name => Environment(name) ?? "set-for-the-container")!;

    private static Task Reconcile(StubRealmAdmin admin, Func<string, string?>? environment = null) =>
        new RealmSettingsReconciler(admin, NullLogger.Instance, environment ?? Environment)
            .ReconcileAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Updates_managed_settings_without_replacing_unmanaged_realm_data()
    {
        var current = Imported();
        current["displayName"] = "Old name";
        current["smtpServer"] = new JsonObject { ["host"] = "custom.example" };
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(1, admin.RealmUpdates);
        Assert.Equal("Group Split", admin.RealmData["displayName"]?.GetValue<string>());
        Assert.Equal("custom.example", admin.RealmData["smtpServer"]?["host"]?.GetValue<string>());
    }

    [Fact]
    public async Task Matching_realm_causes_no_update()
    {
        var admin = new StubRealmAdmin(Imported());

        await Reconcile(admin);

        Assert.Equal(0, admin.RealmUpdates);
        Assert.Equal(0, admin.ClientUpdates);
        Assert.Empty(admin.Writes);
    }

    /// <summary>
    /// The realm every deployment so far was created with: <c>web-app</c> accepting any
    /// redirect URI, from any origin, and any post-logout destination.
    /// </summary>
    [Fact]
    public async Task A_wildcard_redirect_on_an_existing_realm_is_replaced_by_the_web_origin()
    {
        var current = Imported();
        var client = KeycloakRealmDefinition.Client(current, "web-app")!;
        client["redirectUris"] = new JsonArray("*");
        client["webOrigins"] = new JsonArray("*");
        client["attributes"]!["post.logout.redirect.uris"] = "+";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        var updated = admin.Clients["web-app"];
        Assert.Equal(1, admin.ClientUpdates);
        Assert.Equal([$"{Origin}/signin-oidc"], Strings(updated["redirectUris"]));
        Assert.Empty(Strings(updated["webOrigins"]));
        Assert.Equal($"{Origin}/signout-callback-oidc",
            updated["attributes"]!["post.logout.redirect.uris"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_settings_outside_the_allowlist_are_left_alone()
    {
        var current = Imported();
        var client = KeycloakRealmDefinition.Client(current, "web-app")!;
        client["redirectUris"] = new JsonArray("*");
        client["description"] = "Edited by hand";
        client["attributes"]!["pkce.code.challenge.method"] = "plain";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        var updated = admin.Clients["web-app"];
        Assert.Equal("Edited by hand", updated["description"]!.GetValue<string>());
        Assert.Equal("plain", updated["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>());
        Assert.NotNull(updated["protocolMappers"]);
    }

    /// <summary>
    /// Keycloak hands a list of URIs back in its own order. Comparing it as an array would
    /// rewrite the client on every start and make "running it again changes nothing" false.
    /// </summary>
    [Fact]
    public async Task Redirect_uris_differing_only_in_order_or_repetition_are_not_a_change()
    {
        var current = Imported();
        var client = KeycloakRealmDefinition.Client(current, "web-app")!;
        client["redirectUris"] = new JsonArray($"{Origin}/signin-oidc", $"{Origin}/signin-oidc");
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(0, admin.ClientUpdates);
    }

    /// <summary>The post-logout attribute is several URIs joined with <c>##</c>, and a set like the list above.</summary>
    [Fact]
    public async Task A_multi_valued_attribute_in_another_order_is_not_a_change()
    {
        var current = Imported();
        var attributes = KeycloakRealmDefinition.Client(current, "web-app")!["attributes"]!;
        attributes["post.logout.redirect.uris"] =
            $"{Origin}/signout-callback-oidc##{Origin}/signout-callback-oidc";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(0, admin.ClientUpdates);
    }

    /// <summary>
    /// Only attributes are split on <c>##</c>. A realm setting is compared as the string it is,
    /// so a value that differs only in how it would split is still a change.
    /// </summary>
    [Fact]
    public async Task A_realm_setting_is_compared_as_a_whole_string()
    {
        var current = Imported();
        current["displayName"] = "Group Split##Group Split";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(1, admin.RealmUpdates);
        Assert.Equal("Group Split", admin.RealmData["displayName"]!.GetValue<string>());
    }

    private const string AdminConsoleUrl = "http://localhost:8180/idp";

    private static string? DeployedEnvironment(string name) =>
        name == RealmSettingsReconciler.AdminConsoleUrlKey ? AdminConsoleUrl : Environment(name);

    /// <summary>
    /// Deployed, the admin console comes in through an SSH tunnel, and it signs in to the admin
    /// realm -- whose endpoints would otherwise be built on the public origin, where they 404.
    /// </summary>
    [Fact]
    public async Task A_configured_admin_console_address_becomes_the_admin_realm_frontend()
    {
        var admin = new StubRealmAdmin(Imported());

        await Reconcile(admin, DeployedEnvironment);

        Assert.Equal(1, admin.AdminRealmUpdates);
        Assert.Equal(AdminConsoleUrl, admin.AdminRealmData["attributes"]!["frontendUrl"]!.GetValue<string>());
        Assert.Equal("kept", admin.AdminRealmData["attributes"]!["unrelated"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_admin_realm_already_at_the_address_is_not_rewritten()
    {
        var admin = new StubRealmAdmin(Imported());

        await Reconcile(admin, DeployedEnvironment);
        await Reconcile(admin, DeployedEnvironment);

        Assert.Equal(1, admin.AdminRealmUpdates);
    }

    /// <summary>Locally the console is Keycloak's own endpoint, so the admin realm is left exactly as it is.</summary>
    [Fact]
    public async Task Without_an_admin_console_address_the_admin_realm_is_not_touched()
    {
        var admin = new StubRealmAdmin(Imported());

        await Reconcile(admin);

        Assert.Equal(0, admin.AdminRealmReads);
        Assert.Equal(0, admin.AdminRealmUpdates);
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var current = Imported();
        current["ssoSessionIdleTimeout"] = 1;
        KeycloakRealmDefinition.Client(current, "web-app")!["redirectUris"] = new JsonArray("*");
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);
        var writes = admin.Writes.Count;
        await Reconcile(admin);

        Assert.Equal(1, admin.RealmUpdates);
        Assert.Equal(1, admin.ClientUpdates);
        Assert.Equal(writes, admin.Writes.Count);
    }

    private const string BrokerFlow = "link only first broker login";

    /// <summary>
    /// The realm every deployment so far was created with: a profile form in front of the link,
    /// whose email field went straight into the lookup, and a Google provider that accepted an
    /// address Google had not verified.
    /// </summary>
    private static JsonObject ImportedBeforeTheFix()
    {
        var realm = Imported();

        realm["authenticationFlows"] = JsonNode.Parse(
            $$"""
            [
              { "alias": "{{BrokerFlow}}", "topLevel": true, "authenticationExecutions": [
                { "authenticator": "idp-review-profile", "requirement": "REQUIRED", "priority": 10 },
                { "flowAlias": "{{BrokerFlow}} user creation or linking", "requirement": "REQUIRED", "priority": 20 } ] },
              { "alias": "{{BrokerFlow}} user creation or linking", "topLevel": false, "authenticationExecutions": [
                { "authenticator": "idp-detect-existing-broker-user", "requirement": "REQUIRED", "priority": 10 },
                { "authenticator": "idp-auto-link", "requirement": "REQUIRED", "priority": 20 } ] }
            ]
            """);

        var config = Google(realm)["config"]!.AsObject();
        config.Remove("filteredByClaim");
        config.Remove("claimFilterName");
        config.Remove("claimFilterValue");

        return realm;
    }

    private static JsonObject Google(JsonObject realm) => KeycloakRealmDefinition.IdentityProvider(realm, "google")!;

    private static string[] Steps(StubRealmAdmin admin) =>
        admin.Flows[BrokerFlow].Select(execution =>
            $"{execution["level"]} {execution["providerId"] ?? execution["displayName"]} {execution["requirement"]}").ToArray();

    [Fact]
    public async Task The_profile_form_in_front_of_the_link_is_taken_out_of_an_existing_realm()
    {
        var admin = new StubRealmAdmin(ImportedBeforeTheFix());

        await Reconcile(admin);

        Assert.Equal(
            ["0 idp-detect-existing-broker-user REQUIRED", "0 idp-auto-link REQUIRED"],
            Steps(admin));
    }

    [Fact]
    public async Task An_existing_google_provider_is_made_to_require_an_address_google_verified()
    {
        var admin = new StubRealmAdmin(ImportedBeforeTheFix());

        await Reconcile(admin);

        var config = admin.IdentityProviders["google"]["config"]!;
        Assert.Equal(1, admin.IdentityProviderUpdates);
        Assert.Equal("true", config["filteredByClaim"]!.GetValue<string>());
        Assert.Equal("email_verified", config["claimFilterName"]!.GetValue<string>());
        Assert.Equal("true", config["claimFilterValue"]!.GetValue<string>());
        Assert.Equal(BrokerFlow, admin.IdentityProviders["google"]["firstBrokerLoginFlowAlias"]!.GetValue<string>());
    }

    /// <summary>
    /// The provider's credentials and switch belong to the container. The secret only ever
    /// reaches the worker masked, and has to survive being written back that way.
    /// </summary>
    [Fact]
    public async Task The_google_provider_keeps_its_credentials_and_its_switch()
    {
        var current = ImportedBeforeTheFix();
        Google(current)["enabled"] = false;
        Google(current)["config"]!["clientId"] = "deployed-client-id";
        Google(current)["config"]!["clientSecret"] = "deployed-secret";
        Google(current)["config"]!["hostedDomain"] = "edited.example";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        var google = admin.IdentityProviders["google"];
        Assert.False(google["enabled"]!.GetValue<bool>());
        Assert.Equal("deployed-client-id", google["config"]!["clientId"]!.GetValue<string>());
        Assert.Equal("deployed-secret", google["config"]!["clientSecret"]!.GetValue<string>());
        Assert.Equal("edited.example", google["config"]!["hostedDomain"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_step_switched_off_by_hand_is_switched_back_on()
    {
        var admin = new StubRealmAdmin(Imported());
        admin.Flows[BrokerFlow][0]["requirement"] = "DISABLED";

        await Reconcile(admin);

        Assert.Equal(
            ["0 idp-detect-existing-broker-user REQUIRED", "0 idp-auto-link REQUIRED"],
            Steps(admin));
    }

    /// <summary>
    /// Pointing the provider at a flow that does not exist yet, or not in its final shape,
    /// would leave a moment where Google sign-in runs something else.
    /// </summary>
    [Fact]
    public async Task A_missing_flow_is_built_before_the_provider_is_pointed_at_it()
    {
        var current = Imported();
        current["authenticationFlows"] = new JsonArray();
        Google(current)["firstBrokerLoginFlowAlias"] = "first broker login";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(
            [
                $"create {BrokerFlow}",
                "add idp-detect-existing-broker-user",
                "add idp-auto-link",
                "require idp-detect-existing-broker-user REQUIRED",
                "require idp-auto-link REQUIRED",
                "provider google"
            ],
            admin.Writes);
        Assert.Equal(BrokerFlow, admin.IdentityProviders["google"]["firstBrokerLoginFlowAlias"]!.GetValue<string>());
    }

    /// <summary>
    /// Creating the provider would need the client id and secret, which only the container is
    /// given. Without it nobody signs in through Google, so there is nothing to protect, and
    /// failing the run would only hold the API back.
    /// </summary>
    [Fact]
    public async Task A_realm_without_google_is_not_given_one()
    {
        var current = ImportedBeforeTheFix();
        current["identityProviders"] = new JsonArray();
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Empty(admin.IdentityProviders);
        Assert.Equal(
            ["0 idp-detect-existing-broker-user REQUIRED", "0 idp-auto-link REQUIRED"],
            Steps(admin));
    }

    [Fact]
    public async Task A_second_run_over_the_old_flow_changes_nothing()
    {
        var admin = new StubRealmAdmin(ImportedBeforeTheFix());

        await Reconcile(admin);
        var writes = admin.Writes.Count;
        await Reconcile(admin);

        Assert.Equal(writes, admin.Writes.Count);
        Assert.Equal(1, admin.IdentityProviderUpdates);
    }

    /// <summary>
    /// A redirect URI written as <c>${WEB_HOSTNAME}/signin-oidc</c> matches no request, so
    /// passing the placeholder through would break every sign-in without a word. The run has
    /// to fail instead, and before it has written anything.
    /// </summary>
    [Fact]
    public async Task A_missing_origin_fails_the_run_before_anything_is_written()
    {
        var current = Imported();
        current["displayName"] = "Old name";
        var admin = new StubRealmAdmin(current);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Reconcile(admin, _ => null));

        Assert.Contains("WEB_HOSTNAME", failure.Message);
        Assert.Equal(0, admin.RealmUpdates);
        Assert.Equal(0, admin.ClientUpdates);
    }

    [Fact]
    public async Task A_realm_without_the_web_client_is_reported_rather_than_patched()
    {
        var current = Imported();
        current["clients"] = new JsonArray();
        var admin = new StubRealmAdmin(current);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Reconcile(admin));

        Assert.Contains("web-app", failure.Message);
    }

    /// <summary>
    /// The realm every deployment so far was created with: Keycloak's defaults, which record
    /// nothing. Turned on, with the retention, listener and event types the file names.
    /// </summary>
    [Fact]
    public async Task A_realm_recording_no_events_is_made_to_record_them()
    {
        var current = Imported();
        current["eventsEnabled"] = false;
        current.Remove("eventsExpiration");
        current["eventsListeners"] = new JsonArray("jboss-logging");
        current["enabledEventTypes"] = new JsonArray();
        current["adminEventsEnabled"] = false;
        current["adminEventsDetailsEnabled"] = true;
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        var realm = admin.RealmData;
        Assert.Equal(1, admin.RealmUpdates);
        Assert.True(realm["eventsEnabled"]!.GetValue<bool>());
        Assert.Equal(7776000, realm["eventsExpiration"]!.GetValue<long>());
        Assert.Contains("LOGIN", Strings(realm["enabledEventTypes"]));
        Assert.Contains("FEDERATED_IDENTITY_LINK", Strings(realm["enabledEventTypes"]));
        Assert.True(realm["adminEventsEnabled"]!.GetValue<bool>());
        Assert.False(realm["adminEventsDetailsEnabled"]!.GetValue<bool>());
    }

    /// <summary>Keycloak hands the event types back in its own order, which is not a change.</summary>
    [Fact]
    public async Task Event_types_in_another_order_are_not_a_change()
    {
        var current = Imported();
        current["enabledEventTypes"] = new JsonArray(Strings(current["enabledEventTypes"])
            .Reverse().Select(type => (JsonNode?)JsonValue.Create(type)).ToArray());
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(0, admin.RealmUpdates);
    }

    /// <summary>
    /// A browser flow re-pointed at a weaker copy, by hand or by an import, decides how every
    /// sign-in runs. The binding is what is managed, not the flow it names.
    /// </summary>
    [Fact]
    public async Task A_flow_binding_pointed_elsewhere_is_pointed_back()
    {
        var current = Imported();
        current["browserFlow"] = "browser without the password";
        current["firstBrokerLoginFlow"] = "creates accounts";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        Assert.Equal(1, admin.RealmUpdates);
        Assert.Equal("browser", admin.RealmData["browserFlow"]!.GetValue<string>());
        Assert.Equal("first broker login", admin.RealmData["firstBrokerLoginFlow"]!.GetValue<string>());
    }

    /// <summary>
    /// The realm one developer's machine has: imported before the CLI existed, so
    /// <c>groupsplit auth login</c> has no client to ask for a token.
    /// </summary>
    [Fact]
    public async Task A_realm_imported_before_the_cli_existed_is_given_the_cli_client()
    {
        var current = Imported();
        current["clients"]!.AsArray().Remove(KeycloakRealmDefinition.Client(current, "cli"));
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        var cli = admin.Clients["cli"];
        Assert.Equal(["create client cli"], admin.Writes);
        Assert.True(cli["publicClient"]!.GetValue<bool>());
        Assert.Equal("true", cli["attributes"]!["oauth2.device.authorization.grant.enabled"]!.GetValue<string>());
        AssertOnlyTheDeviceGrant(cli);
        AssertAudienceIsApi(cli);
    }

    [Fact]
    public async Task A_cli_client_given_other_grants_is_left_with_the_device_grant_alone()
    {
        var current = Imported();
        var cli = KeycloakRealmDefinition.Client(current, "cli")!;
        cli["standardFlowEnabled"] = true;
        cli["implicitFlowEnabled"] = true;
        cli["directAccessGrantsEnabled"] = true;
        cli["serviceAccountsEnabled"] = true;
        cli["publicClient"] = false;
        cli["attributes"]!["oauth2.device.authorization.grant.enabled"] = "false";
        cli["attributes"]!["oidc.ciba.grant.enabled"] = "true";
        cli["description"] = "Edited by hand";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);

        var updated = admin.Clients["cli"];
        Assert.Equal(1, admin.ClientUpdates);
        Assert.True(updated["publicClient"]!.GetValue<bool>());
        Assert.Equal("true", updated["attributes"]!["oauth2.device.authorization.grant.enabled"]!.GetValue<string>());
        AssertOnlyTheDeviceGrant(updated);
        Assert.Equal("Edited by hand", updated["description"]!.GetValue<string>());
    }

    /// <summary>Without the audience the API refuses every token the CLI brings.</summary>
    [Fact]
    public async Task A_cli_client_without_its_audience_mapper_is_given_it_back()
    {
        var current = Imported();
        KeycloakRealmDefinition.Client(current, "cli")!["protocolMappers"] = new JsonArray();
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);
        await Reconcile(admin);

        Assert.Equal(["create mapper audience-mapper"], admin.Writes);
        AssertAudienceIsApi(admin.Clients["cli"]);
    }

    /// <summary>
    /// The audience pointed at another client is put back; config Keycloak added on its own,
    /// such as the introspection claim a migration gave every mapper, is not a difference.
    /// </summary>
    [Fact]
    public async Task An_audience_mapper_pointed_elsewhere_is_put_back_and_keycloaks_own_entries_kept()
    {
        var current = Imported();
        var config = KeycloakRealmDefinition.ProtocolMapper(
            KeycloakRealmDefinition.Client(current, "cli")!, "audience-mapper")!["config"]!;
        config["included.client.audience"] = "web-app";
        config["introspection.token.claim"] = "true";
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);
        await Reconcile(admin);

        Assert.Equal(["update mapper audience-mapper"], admin.Writes);
        AssertAudienceIsApi(admin.Clients["cli"]);
        Assert.Equal("true", KeycloakRealmDefinition.ProtocolMapper(admin.Clients["cli"], "audience-mapper")!
            ["config"]!["introspection.token.claim"]!.GetValue<string>());
    }

    private static void AssertOnlyTheDeviceGrant(JsonNode cli)
    {
        Assert.False(cli["standardFlowEnabled"]!.GetValue<bool>());
        Assert.False(cli["implicitFlowEnabled"]!.GetValue<bool>());
        Assert.False(cli["directAccessGrantsEnabled"]!.GetValue<bool>());
        Assert.False(cli["serviceAccountsEnabled"]!.GetValue<bool>());
        Assert.Equal("false", cli["attributes"]!["oidc.ciba.grant.enabled"]!.GetValue<string>());
    }

    private static void AssertAudienceIsApi(JsonObject cli)
    {
        var mapper = KeycloakRealmDefinition.ProtocolMapper(cli, "audience-mapper")!;
        Assert.Equal("oidc-audience-mapper", mapper["protocolMapper"]!.GetValue<string>());
        Assert.Equal("api", mapper["config"]!["included.client.audience"]!.GetValue<string>());
        Assert.Equal("true", mapper["config"]!["access.token.claim"]!.GetValue<string>());
    }

    private static string[] Strings(JsonNode? node) =>
        node!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

    private sealed class StubRealmAdmin : IKeycloakRealmAdminClient
    {
        public StubRealmAdmin(JsonObject realm)
        {
            RealmData = realm;

            foreach (var client in realm["clients"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                Store(client);
            }

            foreach (var provider in realm["identityProviders"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                IdentityProviders[provider["alias"]!.GetValue<string>()] = (JsonObject)provider.DeepClone();
            }

            var flows = realm["authenticationFlows"]?.AsArray().OfType<JsonObject>().ToList() ?? [];

            foreach (var flow in flows.Where(flow => flow["topLevel"]?.GetValue<bool>() == true))
            {
                Flows[flow["alias"]!.GetValue<string>()] = Executions(flow, flows, level: 0).ToList();
            }
        }

        /// <summary>
        /// Flattens an imported flow the way Keycloak's executions endpoint lists it: a subflow
        /// is one row, followed by its own executions one level deeper.
        /// </summary>
        private static IEnumerable<JsonObject> Executions(JsonObject flow, List<JsonObject> flows, int level)
        {
            foreach (var execution in flow["authenticationExecutions"]!.AsArray().OfType<JsonObject>()
                         .OrderBy(execution => execution["priority"]!.GetValue<int>()))
            {
                var row = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["requirement"] = execution["requirement"]!.GetValue<string>(),
                    ["level"] = level
                };

                if (execution["flowAlias"]?.GetValue<string>() is { } subflowAlias)
                {
                    row["displayName"] = subflowAlias;
                    row["authenticationFlow"] = true;
                    yield return row;

                    var subflow = flows.Single(candidate => candidate["alias"]!.GetValue<string>() == subflowAlias);

                    foreach (var child in Executions(subflow, flows, level + 1))
                    {
                        yield return child;
                    }
                }
                else
                {
                    row["providerId"] = execution["authenticator"]!.GetValue<string>();
                    yield return row;
                }
            }
        }

        public Dictionary<string, JsonObject> IdentityProviders { get; } = [];

        public Dictionary<string, List<JsonObject>> Flows { get; } = [];

        public int IdentityProviderUpdates { get; private set; }

        /// <summary>Every call that changed a flow, plus the provider updates, in order.</summary>
        public List<string> Writes { get; } = [];

        public int FlowWrites => Writes.Count(write => !write.StartsWith("provider", StringComparison.Ordinal));

        private const string MaskedSecret = "**********";

        public Task<JsonObject?> GetIdentityProviderAsync(string alias, CancellationToken ct = default)
        {
            if (!IdentityProviders.TryGetValue(alias, out var provider)) return Task.FromResult<JsonObject?>(null);

            var copy = (JsonObject)provider.DeepClone();

            if (copy["config"]?["clientSecret"] is not null)
            {
                copy["config"]!["clientSecret"] = MaskedSecret;
            }

            return Task.FromResult<JsonObject?>(copy);
        }

        /// <summary>Keeps the stored secret when the mask comes back, as Keycloak does.</summary>
        public Task UpdateIdentityProviderAsync(JsonObject provider, CancellationToken ct = default)
        {
            var alias = provider["alias"]!.GetValue<string>();
            var copy = (JsonObject)provider.DeepClone();

            if (copy["config"]?["clientSecret"]?.GetValue<string>() == MaskedSecret)
            {
                copy["config"]!["clientSecret"] = IdentityProviders[alias]["config"]!["clientSecret"]!.DeepClone();
            }

            IdentityProviders[alias] = copy;
            IdentityProviderUpdates++;
            Writes.Add($"provider {alias}");
            return Task.CompletedTask;
        }

        public Task<bool> FlowExistsAsync(string alias, CancellationToken ct = default) =>
            Task.FromResult(Flows.ContainsKey(alias));

        public Task CreateFlowAsync(string alias, string description, CancellationToken ct = default)
        {
            Assert.False(Flows.ContainsKey(alias), $"Flow '{alias}' already exists.");

            Flows[alias] = [];
            Writes.Add($"create {alias}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JsonObject>> GetFlowExecutionsAsync(string alias, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JsonObject>>(
                Flows[alias].Select(execution => (JsonObject)execution.DeepClone()).ToList());

        public Task DeleteExecutionAsync(string executionId, CancellationToken ct = default)
        {
            var flow = Flows.Values.Single(executions => executions.Any(execution => Id(execution) == executionId));
            var index = flow.FindIndex(execution => Id(execution) == executionId);
            var level = flow[index]["level"]!.GetValue<int>();
            var count = 1;

            while (index + count < flow.Count && flow[index + count]["level"]!.GetValue<int>() > level)
            {
                count++;
            }

            flow.RemoveRange(index, count);
            Writes.Add($"delete {executionId}");
            return Task.CompletedTask;
        }

        public Task AddExecutionAsync(string flowAlias, string authenticator, CancellationToken ct = default)
        {
            Flows[flowAlias].Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["providerId"] = authenticator,
                ["requirement"] = "DISABLED",
                ["level"] = 0
            });
            Writes.Add($"add {authenticator}");
            return Task.CompletedTask;
        }

        public Task UpdateExecutionAsync(string flowAlias, JsonObject execution, CancellationToken ct = default)
        {
            var flow = Flows[flowAlias];
            var index = flow.FindIndex(existing => Id(existing) == Id(execution));

            flow[index] = (JsonObject)execution.DeepClone();
            Writes.Add($"require {execution["providerId"]} {execution["requirement"]}");
            return Task.CompletedTask;
        }

        private static string Id(JsonObject execution) => execution["id"]!.GetValue<string>();

        public string Realm => "group-split";

        public JsonObject RealmData { get; private set; }
        public Dictionary<string, JsonObject> Clients { get; } = [];

        public int RealmUpdates { get; private set; }
        public int ClientUpdates { get; private set; }

        public Task SignInAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JsonObject> GetRealmAsync(CancellationToken ct = default)
            => Task.FromResult((JsonObject)RealmData.DeepClone());

        public Task UpdateRealmAsync(JsonObject updated, CancellationToken ct = default)
        {
            RealmData = (JsonObject)updated.DeepClone();
            RealmUpdates++;
            return Task.CompletedTask;
        }

        public Task DeleteRealmAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JsonObject?> GetClientAsync(string clientId, CancellationToken ct = default)
            => Task.FromResult(Clients.TryGetValue(clientId, out var client)
                ? (JsonObject?)client.DeepClone()
                : null);

        /// <summary>Keeps the stored protocol mappers whatever the body says, as Keycloak does.</summary>
        public Task UpdateClientAsync(JsonObject client, CancellationToken ct = default)
        {
            var clientId = client["clientId"]!.GetValue<string>();
            Assert.Equal(Clients[clientId]["id"]!.GetValue<string>(), client["id"]!.GetValue<string>());

            var copy = (JsonObject)client.DeepClone();
            copy["protocolMappers"] = Clients[clientId]["protocolMappers"]?.DeepClone();
            Clients[clientId] = copy;
            ClientUpdates++;
            Writes.Add($"client {clientId}");
            return Task.CompletedTask;
        }

        public Task CreateClientAsync(JsonObject client, CancellationToken ct = default)
        {
            var clientId = client["clientId"]!.GetValue<string>();
            Assert.False(Clients.ContainsKey(clientId), $"Client '{clientId}' already exists.");

            Store(client);
            Writes.Add($"create client {clientId}");
            return Task.CompletedTask;
        }

        public Task CreateProtocolMapperAsync(string clientId, JsonObject mapper, CancellationToken ct = default)
        {
            var client = Clients.Values.Single(candidate => candidate["id"]!.GetValue<string>() == clientId);
            var name = mapper["name"]!.GetValue<string>();
            Assert.Null(KeycloakRealmDefinition.ProtocolMapper(client, name));

            if (client["protocolMappers"] is not JsonArray mappers)
            {
                client["protocolMappers"] = mappers = new JsonArray();
            }

            var copy = (JsonObject)mapper.DeepClone();
            copy["id"] = Guid.NewGuid().ToString();
            mappers.Add(copy);
            Writes.Add($"create mapper {name}");
            return Task.CompletedTask;
        }

        public Task UpdateProtocolMapperAsync(string clientId, JsonObject mapper, CancellationToken ct = default)
        {
            var client = Clients.Values.Single(candidate => candidate["id"]!.GetValue<string>() == clientId);
            var mappers = client["protocolMappers"]!.AsArray();
            var index = mappers.Select((existing, i) => (existing, i))
                .Single(pair => pair.existing!["id"]!.GetValue<string>() == mapper["id"]!.GetValue<string>()).i;

            mappers[index] = mapper.DeepClone();
            Writes.Add($"update mapper {mapper["name"]}");
            return Task.CompletedTask;
        }

        /// <summary>Stores a client the way Keycloak does: with internal ids on it and its mappers.</summary>
        private void Store(JsonObject client)
        {
            var copy = (JsonObject)client.DeepClone();
            copy["id"] = Guid.NewGuid().ToString();

            foreach (var mapper in copy["protocolMappers"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                mapper["id"] = Guid.NewGuid().ToString();
            }

            Clients[copy["clientId"]!.GetValue<string>()] = copy;
        }

        public string AdminRealm => "master";

        public JsonObject AdminRealmData { get; private set; } = new()
        {
            ["realm"] = "master",
            ["attributes"] = new JsonObject { ["unrelated"] = "kept" }
        };

        public int AdminRealmReads { get; private set; }
        public int AdminRealmUpdates { get; private set; }

        public Task<JsonObject> GetAdminRealmAsync(CancellationToken ct = default)
        {
            AdminRealmReads++;
            return Task.FromResult((JsonObject)AdminRealmData.DeepClone());
        }

        public Task UpdateAdminRealmAsync(JsonObject realm, CancellationToken ct = default)
        {
            AdminRealmData = (JsonObject)realm.DeepClone();
            AdminRealmUpdates++;
            return Task.CompletedTask;
        }
    }
}

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

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var current = Imported();
        current["ssoSessionIdleTimeout"] = 1;
        KeycloakRealmDefinition.Client(current, "web-app")!["redirectUris"] = new JsonArray("*");
        var admin = new StubRealmAdmin(current);

        await Reconcile(admin);
        await Reconcile(admin);

        Assert.Equal(1, admin.RealmUpdates);
        Assert.Equal(1, admin.ClientUpdates);
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

    private static string[] Strings(JsonNode? node) =>
        node!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

    private sealed class StubRealmAdmin : IKeycloakRealmAdminClient
    {
        public StubRealmAdmin(JsonObject realm)
        {
            RealmData = realm;

            foreach (var client in realm["clients"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var copy = (JsonObject)client.DeepClone();
                copy["id"] = Guid.NewGuid().ToString();
                Clients[copy["clientId"]!.GetValue<string>()] = copy;
            }
        }

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

        public Task UpdateClientAsync(JsonObject client, CancellationToken ct = default)
        {
            var clientId = client["clientId"]!.GetValue<string>();
            Assert.Equal(Clients[clientId]["id"]!.GetValue<string>(), client["id"]!.GetValue<string>());

            Clients[clientId] = (JsonObject)client.DeepClone();
            ClientUpdates++;
            return Task.CompletedTask;
        }
    }
}

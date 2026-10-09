using System.Text.Json.Nodes;
using GroupSplit.Keycloak;

namespace GroupSplit.RealmWorker;

/// <summary>
/// Brings the allowlisted realm, client and identity provider settings, and the first broker
/// login flow, back to the checked-in realm export, and, when an admin console address is
/// configured, points the admin realm at it.
/// </summary>
/// <param name="environment">
/// Resolves the <c>${...}</c> placeholders the export carries, from the same variables the
/// Keycloak container is given for its first-boot import, and reads
/// <see cref="AdminConsoleUrlKey"/>.
/// </param>
public sealed class RealmSettingsReconciler(
    IKeycloakRealmAdminClient keycloak,
    ILogger logger,
    Func<string, string?> environment)
{
    /// <summary>
    /// Where operators reach the admin console, when that is not the public origin. Deployed,
    /// the public origin no longer serves the admin console or the admin realm, and an operator
    /// comes in through an SSH tunnel instead.
    /// </summary>
    public const string AdminConsoleUrlKey = "Realm:AdminConsoleUrl";

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var desired = Desired();

        await ReconcileRealmAsync(desired, ct);

        foreach (var managed in KeycloakRealmDefinition.ManagedClients)
        {
            await ReconcileClientAsync(desired, managed, ct);
        }

        foreach (var managed in KeycloakRealmDefinition.ManagedIdentityProviders)
        {
            await ReconcileIdentityProviderAsync(desired, managed, ct);
        }

        if (environment(AdminConsoleUrlKey) is { Length: > 0 } adminConsoleUrl)
        {
            await ReconcileAdminFrontendAsync(adminConsoleUrl, ct);
        }
    }

    /// <summary>
    /// Gives the admin realm its own frontend URL: the admin console's address.
    /// </summary>
    /// <remarks>
    /// <c>KC_HOSTNAME_ADMIN</c> moves where the console itself is served, but the console signs
    /// in to the admin realm, and a realm's endpoints follow <c>KC_HOSTNAME</c> -- the public
    /// origin -- unless the realm names a frontend URL of its own. Left there, the console's
    /// sign-in and its third-party-cookie check go to the public host, which answers 404 for
    /// the admin realm, and the console hangs on a blank page. Only the admin realm is pointed
    /// elsewhere; the app realm keeps the public origin its users and tokens need.
    /// </remarks>
    private async Task ReconcileAdminFrontendAsync(string adminConsoleUrl, CancellationToken ct)
    {
        var current = await keycloak.GetAdminRealmAsync(ct);

        if (current["attributes"] is not JsonObject attributes)
        {
            current["attributes"] = attributes = new JsonObject();
        }

        if (attributes["frontendUrl"]?.GetValue<string>() == adminConsoleUrl)
        {
            logger.LogInformation("Keycloak admin realm {Realm} already uses the admin console address.",
                keycloak.AdminRealm);
            return;
        }

        attributes["frontendUrl"] = adminConsoleUrl;

        await keycloak.UpdateAdminRealmAsync(current, ct);
        logger.LogInformation("Pointed Keycloak admin realm {Realm} at the admin console address {Url}.",
            keycloak.AdminRealm, adminConsoleUrl);
    }

    /// <summary>
    /// The managed values alone, cut out of the realm export and with their placeholders
    /// resolved -- in one pass, so a missing variable fails the run before anything is
    /// written, and names every variable that is missing rather than the first.
    /// </summary>
    /// <remarks>
    /// Only what is managed is resolved. The export also carries placeholders for SMTP and
    /// Google sign-in, which the Keycloak container is given and this worker is not; resolving
    /// the whole file would demand those secrets for no reason.
    /// </remarks>
    private JsonObject Desired()
    {
        var definition = KeycloakRealmDefinition.Load();
        var managed = Pick(definition, KeycloakRealmDefinition.ManagedSettings);
        var clients = new JsonArray();

        foreach (var client in KeycloakRealmDefinition.ManagedClients)
        {
            var source = KeycloakRealmDefinition.Client(definition, client.ClientId)!;

            // A client that may be created is wanted whole, since creating it writes all of it.
            // Only a client with nothing environment-specific in it is allowed to be.
            if (client.CreateWhenMissing)
            {
                clients.Add(source.DeepClone());
                continue;
            }

            var picked = Pick(source, client.Properties);

            picked["clientId"] = client.ClientId;
            picked["attributes"] = Pick((JsonObject)source["attributes"]!, client.Attributes);
            picked["protocolMappers"] = new JsonArray(client.ProtocolMappers
                .Select(name => KeycloakRealmDefinition.ProtocolMapper(source, name)!.DeepClone())
                .ToArray());
            clients.Add(picked);
        }

        managed["clients"] = clients;

        var providers = new JsonArray();
        var flows = new JsonArray();

        foreach (var provider in KeycloakRealmDefinition.ManagedIdentityProviders)
        {
            var source = KeycloakRealmDefinition.IdentityProvider(definition, provider.Alias)!;
            var picked = Pick(source, provider.Properties);

            picked["alias"] = provider.Alias;
            picked["config"] = Pick((JsonObject)source["config"]!, provider.Config);
            providers.Add(picked);

            var flowAlias = source["firstBrokerLoginFlowAlias"]!.GetValue<string>();
            flows.Add(KeycloakRealmDefinition.Flow(definition, flowAlias)!.DeepClone());
        }

        managed["identityProviders"] = providers;
        managed["authenticationFlows"] = flows;

        return (JsonObject)KeycloakPlaceholders.Expand(managed, environment)!;

        static JsonObject Pick(JsonObject source, IEnumerable<string> names) =>
            new(names.Select(name => KeyValuePair.Create(name, source[name]?.DeepClone())));
    }

    private async Task ReconcileRealmAsync(JsonObject desired, CancellationToken ct)
    {
        var current = await keycloak.GetRealmAsync(ct);
        var changed = Apply(current, desired, KeycloakRealmDefinition.ManagedSettings);

        if (changed.Count == 0)
        {
            logger.LogInformation("Keycloak realm {Realm} already matches the repository settings.", keycloak.Realm);
            return;
        }

        await keycloak.UpdateRealmAsync(current, ct);
        logger.LogInformation(
            "Updated {Count} settings on Keycloak realm {Realm}: {Settings}.",
            changed.Count, keycloak.Realm, string.Join(", ", changed));
    }

    private async Task ReconcileClientAsync(JsonObject desired, ManagedClient managed, CancellationToken ct)
    {
        var wanted = KeycloakRealmDefinition.Client(desired, managed.ClientId)!;
        var current = await keycloak.GetClientAsync(managed.ClientId, ct);

        if (current is null && managed.CreateWhenMissing)
        {
            // A realm imported before the client existed. Nothing in it depends on where the
            // app runs, so the file's definition is the client an import would have created.
            await keycloak.CreateClientAsync((JsonObject)wanted.DeepClone(), ct);
            logger.LogWarning(
                "Keycloak realm {Realm} had no client {ClientId}; created it from the repository definition.",
                keycloak.Realm, managed.ClientId);

            current = await keycloak.GetClientAsync(managed.ClientId, ct)
                ?? throw new InvalidOperationException(
                    $"Created client '{managed.ClientId}' on Keycloak realm {keycloak.Realm}, but it cannot be read back.");
        }

        // Otherwise a realm without the client is one the import never finished, or one
        // somebody edited by hand. Creating a client that depends on the environment here would
        // make this a second importer, so it is reported instead and the run fails.
        if (current is null)
        {
            throw new InvalidOperationException(
                $"Keycloak realm {keycloak.Realm} has no client '{managed.ClientId}'. "
                + "Reset the realm so it is imported again, or create the client from realms.json.");
        }

        var changed = Apply(current, wanted, managed.Properties);

        if (managed.Attributes.Length > 0)
        {
            if (current["attributes"] is not JsonObject attributes)
            {
                current["attributes"] = attributes = new JsonObject();
            }

            changed.AddRange(Apply(attributes, (JsonObject)wanted["attributes"]!, managed.Attributes, multiValued: true)
                .Select(name => $"attributes.{name}"));
        }

        // The client first, written back with the mappers it was read with, and the mappers
        // after it through their own calls.
        if (changed.Count > 0)
        {
            await keycloak.UpdateClientAsync(current, ct);
            logger.LogInformation(
                "Updated {Count} settings on client {ClientId} of Keycloak realm {Realm}: {Settings}.",
                changed.Count, managed.ClientId, keycloak.Realm, string.Join(", ", changed));
        }

        var mappers = await ReconcileProtocolMappersAsync(current, wanted, managed, ct);

        if (changed.Count == 0 && mappers == 0)
        {
            logger.LogInformation(
                "Client {ClientId} on Keycloak realm {Realm} already matches the repository settings.",
                managed.ClientId, keycloak.Realm);
        }
    }

    /// <summary>
    /// Gives each managed protocol mapper the file's type and config values, creating it when
    /// the client has none by that name.
    /// </summary>
    /// <remarks>
    /// Only the config entries the file names are compared. Keycloak adds entries of its own
    /// to a mapper over time -- a migration gave every mapper <c>introspection.token.claim</c>
    /// -- and those are kept rather than fought over on every run.
    /// </remarks>
    /// <returns>How many mappers were written.</returns>
    private async Task<int> ReconcileProtocolMappersAsync(
        JsonObject current, JsonObject wanted, ManagedClient managed, CancellationToken ct)
    {
        var clientId = current["id"]!.GetValue<string>();
        var written = 0;

        foreach (var name in managed.ProtocolMappers)
        {
            var desired = KeycloakRealmDefinition.ProtocolMapper(wanted, name)!;
            var existing = KeycloakRealmDefinition.ProtocolMapper(current, name);

            if (existing is null)
            {
                await keycloak.CreateProtocolMapperAsync(clientId, (JsonObject)desired.DeepClone(), ct);
                logger.LogInformation(
                    "Added protocol mapper {Mapper} to client {ClientId} of Keycloak realm {Realm}.",
                    name, managed.ClientId, keycloak.Realm);
                written++;
                continue;
            }

            var changed = Apply(existing, desired, ["protocol", "protocolMapper"]);

            if (existing["config"] is not JsonObject config)
            {
                existing["config"] = config = new JsonObject();
            }

            var desiredConfig = (JsonObject)desired["config"]!;

            changed.AddRange(Apply(config, desiredConfig, desiredConfig.Select(entry => entry.Key).ToList())
                .Select(key => $"config.{key}"));

            if (changed.Count == 0) continue;

            await keycloak.UpdateProtocolMapperAsync(clientId, existing, ct);
            logger.LogInformation(
                "Updated protocol mapper {Mapper} of client {ClientId} on Keycloak realm {Realm}: {Settings}.",
                name, managed.ClientId, keycloak.Realm, string.Join(", ", changed));
            written++;
        }

        return written;
    }

    private async Task ReconcileIdentityProviderAsync(
        JsonObject desired, ManagedIdentityProvider managed, CancellationToken ct)
    {
        var wanted = KeycloakRealmDefinition.IdentityProvider(desired, managed.Alias)!;
        var flowAlias = wanted["firstBrokerLoginFlowAlias"]!.GetValue<string>();

        // The flow before the provider, so the provider is only ever pointed at a flow that
        // already says what the file says.
        await ReconcileFlowAsync(KeycloakRealmDefinition.Flow(desired, flowAlias)!, ct);

        // Not created when missing: its client id and secret are the container's, and a realm
        // without the provider has nobody signing in through it, so there is nothing to secure.
        var current = await keycloak.GetIdentityProviderAsync(managed.Alias, ct);

        if (current is null)
        {
            logger.LogWarning(
                "Keycloak realm {Realm} has no identity provider {Alias}; its settings were not reconciled.",
                keycloak.Realm, managed.Alias);
            return;
        }

        var changed = Apply(current, wanted, managed.Properties);

        if (current["config"] is not JsonObject config)
        {
            current["config"] = config = new JsonObject();
        }

        changed.AddRange(Apply(config, (JsonObject)wanted["config"]!, managed.Config)
            .Select(name => $"config.{name}"));

        if (changed.Count == 0)
        {
            logger.LogInformation(
                "Identity provider {Alias} on Keycloak realm {Realm} already matches the repository settings.",
                managed.Alias, keycloak.Realm);
            return;
        }

        await keycloak.UpdateIdentityProviderAsync(current, ct);
        logger.LogInformation(
            "Updated {Count} settings on identity provider {Alias} of Keycloak realm {Realm}: {Settings}.",
            changed.Count, managed.Alias, keycloak.Realm, string.Join(", ", changed));
    }

    /// <summary>
    /// Makes a flow run exactly the file's authenticators, in its order, with its requirements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilt rather than patched: a flow that differs at all is emptied and refilled. Keycloak
    /// can only append an execution and nudge it one place at a time, so patching a reordered
    /// or nested flow into shape would be most of an importer. The definition is held to a flat
    /// list of authenticators, which keeps the rebuild to a handful of calls.
    /// </para>
    /// <para>
    /// A sign-in arriving mid-rebuild meets a flow that is empty or short of its last step, so
    /// it ends with no account chosen and fails. It cannot land in the wrong account.
    /// </para>
    /// </remarks>
    private async Task ReconcileFlowAsync(JsonObject flow, CancellationToken ct)
    {
        var alias = flow["alias"]!.GetValue<string>();
        var wanted = KeycloakRealmDefinition.Steps(flow);

        if (!await keycloak.FlowExistsAsync(alias, ct))
        {
            await keycloak.CreateFlowAsync(alias, flow["description"]?.GetValue<string>() ?? "", ct);
            logger.LogInformation("Created authentication flow {Flow} on Keycloak realm {Realm}.", alias, keycloak.Realm);
        }

        var current = await keycloak.GetFlowExecutionsAsync(alias, ct);

        if (Matches(current, wanted))
        {
            logger.LogInformation(
                "Authentication flow {Flow} on Keycloak realm {Realm} already matches the repository.",
                alias, keycloak.Realm);
            return;
        }

        // Only the top level: removing a subflow's execution takes the subflow with it.
        foreach (var execution in current.Where(execution => Level(execution) == 0))
        {
            await keycloak.DeleteExecutionAsync(execution["id"]!.GetValue<string>(), ct);
        }

        foreach (var step in wanted)
        {
            await keycloak.AddExecutionAsync(alias, step.Authenticator, ct);
        }

        var added = await keycloak.GetFlowExecutionsAsync(alias, ct);

        if (added.Count != wanted.Count
            || added.Zip(wanted).Any(pair => pair.First["providerId"]?.GetValue<string>() != pair.Second.Authenticator))
        {
            throw new InvalidOperationException(
                $"Rebuilt authentication flow '{alias}' on Keycloak realm {keycloak.Realm}, but it reads back as "
                + $"[{Describe(added)}] rather than [{string.Join(", ", wanted)}].");
        }

        foreach (var (execution, step) in added.Zip(wanted))
        {
            if (execution["requirement"]?.GetValue<string>() == step.Requirement) continue;

            execution["requirement"] = step.Requirement;
            await keycloak.UpdateExecutionAsync(alias, execution, ct);
        }

        logger.LogInformation(
            "Rebuilt authentication flow {Flow} on Keycloak realm {Realm}. It ran [{Previous}]; it now runs [{Current}].",
            alias, keycloak.Realm, Describe(current), string.Join(", ", wanted));

        static bool Matches(IReadOnlyList<JsonObject> current, IReadOnlyList<FlowStep> wanted) =>
            current.Count == wanted.Count
            && current.Zip(wanted).All(pair =>
                Level(pair.First) == 0
                && pair.First["authenticationFlow"]?.GetValue<bool>() != true
                && pair.First["providerId"]?.GetValue<string>() == pair.Second.Authenticator
                && pair.First["requirement"]?.GetValue<string>() == pair.Second.Requirement);

        static int Level(JsonObject execution) => execution["level"]?.GetValue<int>() ?? 0;

        static string Describe(IEnumerable<JsonObject> executions) =>
            string.Join(", ", executions.Select(execution =>
                $"{execution["providerId"] ?? execution["displayName"]} {execution["requirement"]}"));
    }

    /// <summary>Copies each named value that differs from <paramref name="desired"/> onto <paramref name="current"/>.</summary>
    /// <param name="multiValued">
    /// Whether a string holds several values joined with <c>##</c>, as a client attribute such
    /// as <c>post.logout.redirect.uris</c> does. Only then is it compared as a set; an ordinary
    /// realm setting that happened to contain <c>##</c> must still compare as the string it is.
    /// </param>
    /// <returns>The names that changed.</returns>
    private static List<string> Apply(
        JsonObject current, JsonObject desired, IEnumerable<string> names, bool multiValued = false)
    {
        var changed = new List<string>();

        foreach (var name in names)
        {
            var value = desired[name];

            if (Same(current[name], value, multiValued)) continue;

            current[name] = value?.DeepClone();
            changed.Add(name);
        }

        return changed;
    }

    /// <summary>
    /// Equality as Keycloak means it. A list of URIs is a set, and Keycloak hands it back in
    /// whatever order it stores it, so comparing it as an array would rewrite the client on
    /// every run. A multi-valued attribute is one string joined with <c>##</c>, for the same reason.
    /// </summary>
    private static bool Same(JsonNode? current, JsonNode? desired, bool multiValued)
    {
        if (current is JsonArray currentItems && desired is JsonArray desiredItems)
        {
            return Values(currentItems).SetEquals(Values(desiredItems));
        }

        if (multiValued
            && current is JsonValue currentValue && desired is JsonValue desiredValue
            && currentValue.TryGetValue<string>(out var currentText)
            && desiredValue.TryGetValue<string>(out var desiredText))
        {
            return Split(currentText).SetEquals(Split(desiredText));
        }

        return JsonNode.DeepEquals(current, desired);

        static HashSet<string> Values(JsonArray items) =>
            items.Select(item => item?.ToJsonString() ?? "null").ToHashSet(StringComparer.Ordinal);

        static HashSet<string> Split(string text) =>
            text.Split("##", StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }
}

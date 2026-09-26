using System.Text.Json.Nodes;
using GroupSplit.Keycloak;

namespace GroupSplit.RealmWorker;

/// <summary>Brings the allowlisted realm and client settings back to the checked-in realm export.</summary>
/// <param name="environment">
/// Resolves the <c>${...}</c> placeholders the export carries, from the same variables the
/// Keycloak container is given for its first-boot import.
/// </param>
public sealed class RealmSettingsReconciler(
    IKeycloakRealmAdminClient keycloak,
    ILogger logger,
    Func<string, string?> environment)
{
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var desired = Desired();

        await ReconcileRealmAsync(desired, ct);

        foreach (var managed in KeycloakRealmDefinition.ManagedClients)
        {
            await ReconcileClientAsync(desired, managed, ct);
        }
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
            var picked = Pick(source, client.Properties);

            picked["clientId"] = client.ClientId;
            picked["attributes"] = Pick((JsonObject)source["attributes"]!, client.Attributes);
            clients.Add(picked);
        }

        managed["clients"] = clients;

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

        // A realm without the client is one the import never finished, or one somebody
        // edited by hand. Creating it here would make this a second importer, so it is
        // reported instead and the run fails.
        var current = await keycloak.GetClientAsync(managed.ClientId, ct)
            ?? throw new InvalidOperationException(
                $"Keycloak realm {keycloak.Realm} has no client '{managed.ClientId}'. "
                + "Reset the realm so it is imported again, or create the client from realms.json.");

        var changed = Apply(current, wanted, managed.Properties);

        if (managed.Attributes.Length > 0)
        {
            if (current["attributes"] is not JsonObject attributes)
            {
                current["attributes"] = attributes = new JsonObject();
            }

            changed.AddRange(Apply(attributes, (JsonObject)wanted["attributes"]!, managed.Attributes)
                .Select(name => $"attributes.{name}"));
        }

        if (changed.Count == 0)
        {
            logger.LogInformation(
                "Client {ClientId} on Keycloak realm {Realm} already matches the repository settings.",
                managed.ClientId, keycloak.Realm);
            return;
        }

        await keycloak.UpdateClientAsync(current, ct);
        logger.LogInformation(
            "Updated {Count} settings on client {ClientId} of Keycloak realm {Realm}: {Settings}.",
            changed.Count, managed.ClientId, keycloak.Realm, string.Join(", ", changed));
    }


    /// <summary>Copies each named value that differs from <paramref name="desired"/> onto <paramref name="current"/>.</summary>
    /// <returns>The names that changed.</returns>
    private static List<string> Apply(JsonObject current, JsonObject desired, IEnumerable<string> names)
    {
        var changed = new List<string>();

        foreach (var name in names)
        {
            var value = desired[name];

            if (Same(current[name], value)) continue;

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
    private static bool Same(JsonNode? current, JsonNode? desired)
    {
        if (current is JsonArray currentItems && desired is JsonArray desiredItems)
        {
            return Values(currentItems).SetEquals(Values(desiredItems));
        }

        if (current is JsonValue currentValue && desired is JsonValue desiredValue
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

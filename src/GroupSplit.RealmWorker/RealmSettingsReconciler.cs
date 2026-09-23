using System.Text.Json.Nodes;
using GroupSplit.Keycloak;

namespace GroupSplit.RealmWorker;

/// <summary>Brings the allowlisted realm settings back to the checked-in realm export.</summary>
public sealed class RealmSettingsReconciler(
    IKeycloakRealmAdminClient keycloak,
    ILogger logger)
{
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var desired = KeycloakRealmDefinition.Load();
        var current = await keycloak.GetRealmAsync(ct);
        var changed = new List<string>();

        foreach (var name in KeycloakRealmDefinition.ManagedSettings)
        {
            var value = desired[name];

            if (JsonNode.DeepEquals(current[name], value))
            {
                continue;
            }

            current[name] = value?.DeepClone();
            changed.Add(name);
        }

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
}

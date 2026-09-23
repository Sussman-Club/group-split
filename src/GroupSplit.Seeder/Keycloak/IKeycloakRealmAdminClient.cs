using System.Text.Json.Nodes;

namespace GroupSplit.Seeder.Keycloak;

/// <summary>The realm-level operations used by the settings reconciler and local reset.</summary>
public interface IKeycloakRealmAdminClient
{
    string Realm { get; }

    Task SignInAsync(CancellationToken ct = default);

    Task<JsonObject> GetRealmAsync(CancellationToken ct = default);

    Task UpdateRealmAsync(JsonObject realm, CancellationToken ct = default);

    Task DeleteRealmAsync(CancellationToken ct = default);
}

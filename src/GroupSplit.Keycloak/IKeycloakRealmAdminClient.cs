using System.Text.Json.Nodes;

namespace GroupSplit.Keycloak;

/// <summary>The realm-level operations used by the settings reconciler and local reset.</summary>
public interface IKeycloakRealmAdminClient
{
    string Realm { get; }

    Task SignInAsync(CancellationToken ct = default);

    Task<JsonObject> GetRealmAsync(CancellationToken ct = default);

    Task UpdateRealmAsync(JsonObject realm, CancellationToken ct = default);

    Task DeleteRealmAsync(CancellationToken ct = default);

    /// <summary>The client with this <c>clientId</c> in the app's realm, or null when there is none.</summary>
    Task<JsonObject?> GetClientAsync(string clientId, CancellationToken ct = default);

    /// <summary>Replaces the client whose internal <c>id</c> the representation carries.</summary>
    Task UpdateClientAsync(JsonObject client, CancellationToken ct = default);

    /// <summary>The realm the bootstrap admin signs in to -- <c>master</c>, unless configured otherwise.</summary>
    string AdminRealm { get; }

    Task<JsonObject> GetAdminRealmAsync(CancellationToken ct = default);

    Task UpdateAdminRealmAsync(JsonObject realm, CancellationToken ct = default);
}

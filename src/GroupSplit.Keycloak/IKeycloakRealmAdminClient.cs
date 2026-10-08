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

    /// <summary>The identity provider with this alias in the app's realm, or null when there is none.</summary>
    /// <remarks>Its client secret comes back masked, and Keycloak keeps the stored one when the mask is sent back.</remarks>
    Task<JsonObject?> GetIdentityProviderAsync(string alias, CancellationToken ct = default);

    Task UpdateIdentityProviderAsync(JsonObject provider, CancellationToken ct = default);

    /// <summary>Whether the app's realm has an authentication flow with this alias.</summary>
    Task<bool> FlowExistsAsync(string alias, CancellationToken ct = default);

    /// <summary>Creates an empty top-level flow.</summary>
    Task CreateFlowAsync(string alias, string description, CancellationToken ct = default);

    /// <summary>
    /// Every execution of the flow, in the order Keycloak runs them, as Keycloak's
    /// execution-info representation: <c>id</c>, <c>providerId</c>, <c>requirement</c>,
    /// <c>level</c>, and <c>authenticationFlow</c> for a subflow.
    /// </summary>
    Task<IReadOnlyList<JsonObject>> GetFlowExecutionsAsync(string alias, CancellationToken ct = default);

    /// <summary>Removes an execution from its flow, and the subflow along with it when it is one.</summary>
    Task DeleteExecutionAsync(string executionId, CancellationToken ct = default);

    /// <summary>Appends an authenticator to the end of a flow. Keycloak adds it disabled.</summary>
    Task AddExecutionAsync(string flowAlias, string authenticator, CancellationToken ct = default);

    /// <summary>Writes back an execution read from <see cref="GetFlowExecutionsAsync"/>, e.g. with a new requirement.</summary>
    Task UpdateExecutionAsync(string flowAlias, JsonObject execution, CancellationToken ct = default);

    /// <summary>The realm the bootstrap admin signs in to -- <c>master</c>, unless configured otherwise.</summary>
    string AdminRealm { get; }

    Task<JsonObject> GetAdminRealmAsync(CancellationToken ct = default);

    Task UpdateAdminRealmAsync(JsonObject realm, CancellationToken ct = default);
}

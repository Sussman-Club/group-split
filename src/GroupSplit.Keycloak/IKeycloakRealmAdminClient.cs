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
    /// <remarks>Its protocol mappers are left as they are; they have calls of their own.</remarks>
    Task UpdateClientAsync(JsonObject client, CancellationToken ct = default);

    /// <summary>
    /// Creates a client in the app's realm from a realm-export representation, protocol
    /// mappers included. One with service accounts on gets its service account user from
    /// Keycloak as it is created.
    /// </summary>
    Task CreateClientAsync(JsonObject client, CancellationToken ct = default);

    /// <summary>Adds a protocol mapper to the client with this internal <c>id</c>.</summary>
    Task CreateProtocolMapperAsync(string clientId, JsonObject mapper, CancellationToken ct = default);

    /// <summary>Replaces the protocol mapper whose <c>id</c> the representation carries.</summary>
    Task UpdateProtocolMapperAsync(string clientId, JsonObject mapper, CancellationToken ct = default);

    /// <summary>The secret of the confidential client whose internal <c>id</c> this is.</summary>
    Task<string?> GetClientSecretAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The names of the <paramref name="roleClientId"/> roles -- <c>realm-management</c>, say --
    /// that the service account of the client with internal <paramref name="id"/> holds directly.
    /// </summary>
    Task<IReadOnlyList<string>> GetServiceAccountRolesAsync(string id, string roleClientId, CancellationToken ct = default);

    Task GrantServiceAccountRolesAsync(string id, string roleClientId, IReadOnlyList<string> roles, CancellationToken ct = default);

    Task RevokeServiceAccountRolesAsync(string id, string roleClientId, IReadOnlyList<string> roles, CancellationToken ct = default);

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

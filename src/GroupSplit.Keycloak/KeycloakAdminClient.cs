using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GroupSplit.Keycloak;

/// <summary>
/// The slice of Keycloak's Admin REST API the app needs: sign in, look a user up, create one,
/// delete one, and reconcile realm-level settings.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a package: four calls against a documented, stable API
/// is less to own than a dependency. The base address is
/// the Keycloak resource, resolved by service discovery from the AppHost's reference.
/// </remarks>
public sealed class KeycloakAdminClient(
    HttpClient http,
    IOptions<KeycloakAdminOptions> options,
    ILogger<KeycloakAdminClient> logger) : IKeycloakAdminClient, IKeycloakRealmAdminClient, IKeycloakUserRemoval
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Re-authenticated well inside the shortest admin token lifetime Keycloak ships with, so
    /// a long seeding run cannot fail on an expired token half way through.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(30);

    private readonly KeycloakAdminOptions _options = options.Value;

    private string? _token;
    private DateTimeOffset _tokenObtainedAt;

    /// <summary>The realm's default role, read once. It cannot change under a seeding run.</summary>
    private KeycloakRole? _defaultRole;

    public string Realm => KeycloakRealmDefinition.RealmName;

    public Task<JsonObject> GetRealmAsync(CancellationToken ct = default) => ReadRealmAsync(Realm, ct);

    public Task UpdateRealmAsync(JsonObject realm, CancellationToken ct = default) =>
        WriteRealmAsync(Realm, realm, ct);

    public string AdminRealm => _options.AdminRealm;

    public Task<JsonObject> GetAdminRealmAsync(CancellationToken ct = default) => ReadRealmAsync(AdminRealm, ct);

    public Task UpdateAdminRealmAsync(JsonObject realm, CancellationToken ct = default) =>
        WriteRealmAsync(AdminRealm, realm, ct);

    public async Task<JsonObject?> GetClientAsync(string clientId, CancellationToken ct = default)
    {
        var query = $"admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}";

        using var request = await AuthorizedAsync(HttpMethod.Get, query, ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read client {clientId} on realm {Realm}", ct);

        // The filter is an exact match on clientId, which the realm keeps unique.
        var matches = await response.Content.ReadFromJsonAsync<List<JsonObject>>(Json, ct);

        return matches?.FirstOrDefault();
    }

    public async Task UpdateClientAsync(JsonObject client, CancellationToken ct = default)
    {
        var id = client["id"]?.GetValue<string>()
                 ?? throw new ArgumentException("The client representation carries no id.", nameof(client));

        using var request = await AuthorizedAsync(HttpMethod.Put, $"admin/realms/{Realm}/clients/{id}", ct);
        request.Content = JsonContent.Create(client, options: Json);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"update client {client["clientId"]} on realm {Realm}", ct);
    }

    public Task CreateClientAsync(JsonObject client, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"admin/realms/{Realm}/clients", client,
            $"create client {client["clientId"]} on realm {Realm}", ct);

    public Task CreateProtocolMapperAsync(string clientId, JsonObject mapper, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"admin/realms/{Realm}/clients/{clientId}/protocol-mappers/models", mapper,
            $"add protocol mapper {mapper["name"]} to client {clientId} on realm {Realm}", ct);

    public Task UpdateProtocolMapperAsync(string clientId, JsonObject mapper, CancellationToken ct = default)
    {
        var id = mapper["id"]?.GetValue<string>()
                 ?? throw new ArgumentException("The protocol mapper representation carries no id.", nameof(mapper));

        return SendAsync(HttpMethod.Put, $"admin/realms/{Realm}/clients/{clientId}/protocol-mappers/models/{id}", mapper,
            $"update protocol mapper {mapper["name"]} of client {clientId} on realm {Realm}", ct);
    }

    public async Task<string?> GetClientSecretAsync(string id, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"admin/realms/{Realm}/clients/{id}/client-secret", ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read the secret of client {id} on realm {Realm}", ct);

        var credential = await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct);

        return credential?["value"]?.GetValue<string>();
    }

    public async Task<IReadOnlyList<string>> GetServiceAccountRolesAsync(
        string id, string roleClientId, CancellationToken ct = default)
    {
        var mappings = await ServiceAccountMappingsAsync(id, roleClientId, ct);

        using var request = await AuthorizedAsync(HttpMethod.Get, mappings, ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read the {roleClientId} roles of client {id}'s service account", ct);

        var roles = await response.Content.ReadFromJsonAsync<List<KeycloakRole>>(Json, ct) ?? [];

        return roles.Select(role => role.Name).ToList();
    }

    public Task GrantServiceAccountRolesAsync(
        string id, string roleClientId, IReadOnlyList<string> roles, CancellationToken ct = default) =>
        MapServiceAccountRolesAsync(HttpMethod.Post, id, roleClientId, roles, "grant", ct);

    public Task RevokeServiceAccountRolesAsync(
        string id, string roleClientId, IReadOnlyList<string> roles, CancellationToken ct = default) =>
        MapServiceAccountRolesAsync(HttpMethod.Delete, id, roleClientId, roles, "revoke", ct);

    /// <summary>
    /// Adds or removes client roles on a service account. Keycloak takes whole role
    /// representations rather than names, so each named role is read first -- and a name the
    /// role client does not have fails here, saying which, rather than being silently skipped.
    /// </summary>
    private async Task MapServiceAccountRolesAsync(
        HttpMethod method, string id, string roleClientId, IReadOnlyList<string> names, string verb, CancellationToken ct)
    {
        if (names.Count == 0) return;

        var roleClient = await ClientUuidAsync(roleClientId, ct);
        var roles = new List<KeycloakRole>();

        foreach (var name in names)
        {
            using var read = await AuthorizedAsync(
                HttpMethod.Get, $"admin/realms/{Realm}/clients/{roleClient}/roles/{Uri.EscapeDataString(name)}", ct);
            using var found = await http.SendAsync(read, ct);

            await ThrowOnFailure(found, $"read role {roleClientId}/{name} on realm {Realm}", ct);

            roles.Add(await found.Content.ReadFromJsonAsync<KeycloakRole>(Json, ct)
                      ?? throw new InvalidOperationException($"Keycloak returned no role {roleClientId}/{name}."));
        }

        using var request = await AuthorizedAsync(method, await ServiceAccountMappingsAsync(id, roleClientId, ct), ct);
        request.Content = JsonContent.Create<IReadOnlyList<KeycloakRole>>(roles, options: Json);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response,
            $"{verb} {string.Join(", ", names)} on client {id}'s service account on realm {Realm}", ct);
    }

    /// <summary>Where a client's service account keeps its mappings to another client's roles.</summary>
    private async Task<string> ServiceAccountMappingsAsync(string id, string roleClientId, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(
            HttpMethod.Get, $"admin/realms/{Realm}/clients/{id}/service-account-user", ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read the service account of client {id} on realm {Realm}", ct);

        var account = await response.Content.ReadFromJsonAsync<KeycloakUserSummary>(Json, ct)
                      ?? throw new InvalidOperationException($"Keycloak returned no service account for client {id}.");

        return $"admin/realms/{Realm}/users/{account.Id}/role-mappings/clients/{await ClientUuidAsync(roleClientId, ct)}";
    }

    private async Task<string> ClientUuidAsync(string clientId, CancellationToken ct) =>
        (await GetClientAsync(clientId, ct))?["id"]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Keycloak realm {Realm} has no client '{clientId}'.");

    public async Task<JsonObject?> GetIdentityProviderAsync(string alias, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(
            HttpMethod.Get, $"admin/realms/{Realm}/identity-provider/instances/{Uri.EscapeDataString(alias)}", ct);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowOnFailure(response, $"read identity provider {alias} on realm {Realm}", ct);

        return await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct);
    }

    public async Task UpdateIdentityProviderAsync(JsonObject provider, CancellationToken ct = default)
    {
        var alias = provider["alias"]?.GetValue<string>()
                    ?? throw new ArgumentException("The identity provider representation carries no alias.", nameof(provider));

        await SendAsync(HttpMethod.Put, $"admin/realms/{Realm}/identity-provider/instances/{Uri.EscapeDataString(alias)}",
            provider, $"update identity provider {alias} on realm {Realm}", ct);
    }

    public async Task<bool> FlowExistsAsync(string alias, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"admin/realms/{Realm}/authentication/flows", ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"list the authentication flows of realm {Realm}", ct);

        var flows = await response.Content.ReadFromJsonAsync<List<JsonObject>>(Json, ct) ?? [];

        return flows.Any(flow => flow["alias"]?.GetValue<string>() == alias);
    }

    public Task CreateFlowAsync(string alias, string description, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"admin/realms/{Realm}/authentication/flows",
            new JsonObject
            {
                ["alias"] = alias,
                ["description"] = description,
                ["providerId"] = "basic-flow",
                ["topLevel"] = true,
                ["builtIn"] = false
            },
            $"create authentication flow {alias} on realm {Realm}", ct);

    public async Task<IReadOnlyList<JsonObject>> GetFlowExecutionsAsync(string alias, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, FlowExecutions(alias), ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read the executions of flow {alias} on realm {Realm}", ct);

        return await response.Content.ReadFromJsonAsync<List<JsonObject>>(Json, ct) ?? [];
    }

    public Task DeleteExecutionAsync(string executionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"admin/realms/{Realm}/authentication/executions/{executionId}",
            content: null, $"delete execution {executionId} on realm {Realm}", ct);

    public Task AddExecutionAsync(string flowAlias, string authenticator, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{FlowExecutions(flowAlias)}/execution",
            new JsonObject { ["provider"] = authenticator },
            $"add {authenticator} to flow {flowAlias} on realm {Realm}", ct);

    public Task UpdateExecutionAsync(string flowAlias, JsonObject execution, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, FlowExecutions(flowAlias), execution,
            $"update execution {execution["providerId"]} of flow {flowAlias} on realm {Realm}", ct);

    private string FlowExecutions(string alias) =>
        $"admin/realms/{Realm}/authentication/flows/{Uri.EscapeDataString(alias)}/executions";

    private async Task SendAsync(HttpMethod method, string uri, JsonObject? content, string attempt, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(method, uri, ct);

        if (content is not null)
        {
            request.Content = JsonContent.Create(content, options: Json);
        }

        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, attempt, ct);
    }

    private async Task<JsonObject> ReadRealmAsync(string realm, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"admin/realms/{realm}", ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read the realm {realm}", ct);

        return await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct)
            ?? throw new InvalidOperationException($"Keycloak returned no representation for realm {realm}.");
    }

    private async Task WriteRealmAsync(string realm, JsonObject representation, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Put, $"admin/realms/{realm}", ct);
        request.Content = JsonContent.Create(representation, options: Json);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"update the settings on realm {realm}", ct);
    }

    public async Task DeleteRealmAsync(CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Delete, $"admin/realms/{Realm}", ct);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return;

        await ThrowOnFailure(response, $"delete realm {Realm}", ct);
    }

    /// <summary>
    /// Signs in, confirming the credentials work before any user is touched. A failure here is
    /// worth reporting on its own: it means the AppHost did not pass the credentials through,
    /// not that a particular account is wrong.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(force: true, ct);
        logger.LogInformation("Signed in to Keycloak as {AdminUser} on realm {AdminRealm}.",
            _options.UsesClientCredentials ? _options.AdminClientId : _options.AdminUser, _options.AdminRealm);
    }

    /// <summary>The user with this id, or null when the realm has none.</summary>
    public async Task<KeycloakUserSummary?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"admin/realms/{Realm}/users/{id}", ct);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowOnFailure(response, $"look up user {id}", ct);

        return await response.Content.ReadFromJsonAsync<KeycloakUserSummary>(Json, ct);
    }

    /// <summary>
    /// The user holding this email, or null. Exact rather than the default prefix search, so
    /// "omar@test.com" cannot match somebody else's longer address.
    /// </summary>
    public async Task<KeycloakUserSummary?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var query = $"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true";

        using var request = await AuthorizedAsync(HttpMethod.Get, query, ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"look up the account for {email}", ct);

        var matches = await response.Content.ReadFromJsonAsync<List<KeycloakUserSummary>>(Json, ct);

        return matches?.FirstOrDefault();
    }

    /// <summary>
    /// Creates the account, keeping the id it carries and granting the realm's default role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through partial import rather than <c>POST /users</c> on purpose: creating a user
    /// discards a supplied id and mints a new one, which would leave the account's subject
    /// with nothing in the app database to match. Partial import keeps the id, the same way
    /// importing a realm export does.
    /// </para>
    /// <para>
    /// The default role is the price of that choice, and it is added here rather than left to
    /// the caller. <c>POST /users</c> grants <c>default-roles-{realm}</c> as a side effect;
    /// partial import grants only what the representation lists. That composite is what
    /// carries the <c>account</c> client roles <c>view-profile</c> and <c>manage-account</c>,
    /// so an account created without it can sign in to the app perfectly well -- the API
    /// authorises on the token's subject -- and still meets nothing but 401s on Keycloak's
    /// own account console.
    /// </para>
    /// </remarks>
    public async Task CreateAsync(KeycloakUser user, CancellationToken ct = default)
    {
        var defaultRole = await DefaultRoleAsync(ct);

        if (!user.RealmRoles.Contains(defaultRole.Name, StringComparer.Ordinal))
        {
            user = user with { RealmRoles = [defaultRole.Name, .. user.RealmRoles] };
        }

        using var request = await AuthorizedAsync(HttpMethod.Post, $"admin/realms/{Realm}/partialImport", ct);
        request.Content = JsonContent.Create(new KeycloakPartialImport { Users = [user] }, options: Json);

        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"create the account for {user.Email}", ct);

        var result = await response.Content.ReadFromJsonAsync<KeycloakPartialImportResult>(Json, ct);

        // A partial import that adds nothing answers 200 all the same, so the counts are the
        // only place a silent no-op would show.
        if (result is not { Added: > 0 })
        {
            throw new InvalidOperationException(
                $"Keycloak accepted the import of {user.Email} without creating it "
                + $"(added {result?.Added ?? 0}, skipped {result?.Skipped ?? 0}, overwritten {result?.Overwritten ?? 0}).");
        }
    }

    /// <summary>
    /// Grants the realm's default role to an account that does not already hold it.
    /// </summary>
    /// <remarks>
    /// For accounts this seeder created before it started granting the role: the realm keeps
    /// its users in a volume, so they outlive the fix, and the seeder leaves an account that
    /// already carries the seeded id alone. Without this they would stay locked out of the
    /// account console until somebody deleted the volume.
    /// </remarks>
    /// <returns>Whether the role had to be granted.</returns>
    public async Task<bool> EnsureDefaultRoleAsync(string id, CancellationToken ct = default)
    {
        var defaultRole = await DefaultRoleAsync(ct);
        var mappings = $"admin/realms/{Realm}/users/{id}/role-mappings/realm";

        using (var read = await AuthorizedAsync(HttpMethod.Get, mappings, ct))
        using (var response = await http.SendAsync(read, ct))
        {
            await ThrowOnFailure(response, $"read the roles of user {id}", ct);

            var granted = await response.Content.ReadFromJsonAsync<List<KeycloakRole>>(Json, ct) ?? [];

            if (granted.Any(role => string.Equals(role.Name, defaultRole.Name, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        using var request = await AuthorizedAsync(HttpMethod.Post, mappings, ct);
        request.Content = JsonContent.Create<IReadOnlyList<KeycloakRole>>([defaultRole], options: Json);

        using var grant = await http.SendAsync(request, ct);

        await ThrowOnFailure(grant, $"grant {defaultRole.Name} to user {id}", ct);

        return true;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Delete, $"admin/realms/{Realm}/users/{id}", ct);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return;

        await ThrowOnFailure(response, $"delete user {id}", ct);
    }

    /// <summary>
    /// Ends every session the user holds, then deletes them. Done when there is no such user,
    /// whichever step finds that out.
    /// </summary>
    /// <remarks>
    /// Keycloak drops a user's sessions when it deletes the user, so the sign-out is the belt
    /// to that brace rather than the thing that does the work: it is the documented way to end
    /// sessions, and it is what tells the clients that hold them. Sign-out first, because a
    /// sign-out after the delete only ever answers 404.
    /// </remarks>
    public async Task RemoveUserAsync(string id, CancellationToken ct = default)
    {
        using (var request = await AuthorizedAsync(HttpMethod.Post, $"admin/realms/{Realm}/users/{id}/logout", ct))
        using (var response = await http.SendAsync(request, ct))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;

            await ThrowOnFailure(response, $"end the sessions of user {id}", ct);
        }

        await DeleteAsync(id, ct);
    }

    /// <summary>
    /// The realm's default role. Read from the realm rather than assembled as
    /// <c>default-roles-{realm}</c>: that is only Keycloak's naming convention, and a realm
    /// is free to point its default at a role called something else.
    /// </summary>
    private async Task<KeycloakRole> DefaultRoleAsync(CancellationToken ct)
    {
        if (_defaultRole is not null) return _defaultRole;

        using var request = await AuthorizedAsync(HttpMethod.Get, $"admin/realms/{Realm}", ct);
        using var response = await http.SendAsync(request, ct);

        await ThrowOnFailure(response, $"read the realm {Realm}", ct);

        var realm = await response.Content.ReadFromJsonAsync<KeycloakRealmSummary>(Json, ct);

        return _defaultRole = realm?.DefaultRole
            ?? throw new InvalidOperationException(
                $"Realm {Realm} names no default role, so a seeded account would have none of the "
                + "account-console permissions every other account in the realm gets.");
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string uri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await EnsureTokenAsync(force: false, ct));
        return request;
    }

    private async Task<string> EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!force
            && _token is not null
            && DateTimeOffset.UtcNow - _tokenObtainedAt < TokenLifetime)
        {
            return _token;
        }

        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Keycloak admin credentials are not configured.");
        }

        var grant = _options.UsesClientCredentials
            ? new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.AdminClientId,
                ["client_secret"] = _options.AdminClientSecret!
            }
            : new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = _options.AdminClientId,
                ["username"] = _options.AdminUser!,
                ["password"] = _options.AdminPassword!
            };

        using var response = await http.PostAsync(
            $"realms/{_options.AdminRealm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(grant),
            ct);

        await ThrowOnFailure(response, "sign in to Keycloak", ct);

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);

        if (string.IsNullOrEmpty(token?.AccessToken))
        {
            throw new InvalidOperationException("Keycloak returned no access token for the admin sign-in.");
        }

        _token = token.AccessToken;
        _tokenObtainedAt = DateTimeOffset.UtcNow;

        return _token;
    }

    /// <summary>
    /// Turns a failed call into an exception naming what was being attempted and what
    /// Keycloak said. The body is included because Keycloak puts the actual reason there --
    /// a password policy violation, say -- and the status alone never explains it.
    /// </summary>
    private static async Task ThrowOnFailure(HttpResponseMessage response, string attempt, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new InvalidOperationException(
            $"Could not {attempt}: Keycloak answered {(int)response.StatusCode} {response.ReasonPhrase}. {body}".Trim());
    }

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    }
}

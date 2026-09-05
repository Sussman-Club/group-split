using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GroupSplit.Seeder.Options;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Keycloak;

/// <summary>
/// The slice of Keycloak's Admin REST API the seeder needs: sign in as the bootstrap admin,
/// look a user up, create one, delete one.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a package: four calls against a documented, stable API
/// is less to own than a dependency, and the seeder is the only caller. The base address is
/// the Keycloak resource, resolved by service discovery from the AppHost's reference.
/// </remarks>
public sealed class KeycloakAdminClient(
    HttpClient http,
    IOptions<KeycloakSeedOptions> options,
    ILogger<KeycloakAdminClient> logger) : IKeycloakAdminClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Re-authenticated well inside the shortest admin token lifetime Keycloak ships with, so
    /// a long seeding run cannot fail on an expired token half way through.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(30);

    private readonly KeycloakSeedOptions _options = options.Value;

    private string? _token;
    private DateTimeOffset _tokenObtainedAt;

    /// <summary>The realm's default role, read once. It cannot change under a seeding run.</summary>
    private KeycloakRole? _defaultRole;

    public string Realm => _options.Realm;

    /// <summary>
    /// Signs in as the bootstrap admin, confirming the credentials work before any user is
    /// touched. A failure here is worth reporting on its own: it means the AppHost did not
    /// pass the credentials through, not that a particular account is wrong.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        await EnsureTokenAsync(force: true, ct);
        logger.LogInformation("Signed in to Keycloak as {AdminUser} on realm {AdminRealm}.",
            _options.AdminUser, _options.AdminRealm);
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
                "Keycloak admin credentials are not configured; nothing can be seeded into the realm.");
        }

        using var response = await http.PostAsync(
            $"realms/{_options.AdminRealm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = _options.AdminClientId,
                ["username"] = _options.AdminUser!,
                ["password"] = _options.AdminPassword!
            }),
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

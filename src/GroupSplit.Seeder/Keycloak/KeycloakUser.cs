using System.Text.Json.Serialization;

namespace GroupSplit.Seeder.Keycloak;

/// <summary>
/// The subset of Keycloak's user representation the seeder writes.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the whole point, and it is why these go through partial import rather
/// than <c>POST /users</c>: creating a user ignores a supplied id and mints a fresh one, while
/// partial import keeps it, exactly as a realm export round-trips. With the id kept, the
/// account's subject matches the identity id the app database was seeded with.
/// </remarks>
public sealed record KeycloakUser
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("username")] public required string Username { get; init; }

    [JsonPropertyName("email")] public required string Email { get; init; }

    [JsonPropertyName("firstName")] public string? FirstName { get; init; }

    [JsonPropertyName("lastName")] public string? LastName { get; init; }

    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    /// <summary>
    /// True for demo accounts: the realm does not require verification, and a local Keycloak
    /// sends its mail to Mailpit, so leaving it false would only add a step to signing in.
    /// </summary>
    [JsonPropertyName("emailVerified")] public bool EmailVerified { get; init; } = true;

    [JsonPropertyName("credentials")] public IReadOnlyList<KeycloakCredential> Credentials { get; init; } = [];

    /// <summary>
    /// Realm roles the account is created with, by name.
    /// <para>
    /// Not something a caller has to fill in: <see cref="KeycloakAdminClient.CreateAsync"/>
    /// puts the realm's default role here, because partial import grants nothing on its own.
    /// </para>
    /// </summary>
    [JsonPropertyName("realmRoles")] public IReadOnlyList<string> RealmRoles { get; init; } = [];
}

public sealed record KeycloakCredential
{
    [JsonPropertyName("type")] public string Type { get; init; } = "password";

    [JsonPropertyName("value")] public required string Value { get; init; }

    /// <summary>Not temporary: a demo account that demands a password change at first sign-in is a nuisance.</summary>
    [JsonPropertyName("temporary")] public bool Temporary { get; init; }
}

/// <summary>
/// A role, in the shape Keycloak both returns and expects back when granting one.
/// </summary>
/// <remarks>
/// Granting takes the whole representation rather than a name, so the one the realm hands
/// back is passed through unchanged.
/// </remarks>
public sealed record KeycloakRole
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("description")] public string? Description { get; init; }

    [JsonPropertyName("composite")] public bool Composite { get; init; }

    [JsonPropertyName("clientRole")] public bool ClientRole { get; init; }

    [JsonPropertyName("containerId")] public string? ContainerId { get; init; }
}

/// <summary>The slice of a realm the seeder reads: which role every account starts with.</summary>
public sealed record KeycloakRealmSummary
{
    [JsonPropertyName("defaultRole")] public KeycloakRole? DefaultRole { get; init; }
}

/// <summary>What a lookup hands back. Only the fields the seeder decides on.</summary>
public sealed record KeycloakUserSummary
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("username")] public string? Username { get; init; }

    [JsonPropertyName("email")] public string? Email { get; init; }
}

/// <summary>The body of a partial import.</summary>
public sealed record KeycloakPartialImport
{
    /// <summary>
    /// <c>FAIL</c> rather than <c>OVERWRITE</c>: the seeder decides for itself what to do
    /// about an account already holding a seeded email, and says so in the log. Letting the
    /// import quietly overwrite would hide the one case worth knowing about.
    /// </summary>
    [JsonPropertyName("ifResourceExists")] public string IfResourceExists { get; init; } = "FAIL";

    [JsonPropertyName("users")] public required IReadOnlyList<KeycloakUser> Users { get; init; }
}

public sealed record KeycloakPartialImportResult
{
    [JsonPropertyName("added")] public int Added { get; init; }

    [JsonPropertyName("skipped")] public int Skipped { get; init; }

    [JsonPropertyName("overwritten")] public int Overwritten { get; init; }
}

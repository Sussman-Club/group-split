using GroupSplit.Keycloak;

namespace GroupSplit.Seeder.Options;

/// <summary>
/// What the seeder needs to create the demo accounts in Keycloak. The AppHost supplies the
/// admin credentials from the Keycloak resource's own parameters, so nothing is committed;
/// absent credentials mean the Keycloak half of the seeding is simply skipped.
/// </summary>
public sealed class KeycloakSeedOptions : KeycloakAdminOptions
{
    /// <summary>
    /// The password every seeded account gets, unless its seed entry names its own. It has
    /// to satisfy the realm's password policy (eight characters, and neither the username
    /// nor the email).
    /// </summary>
    public string DefaultPassword { get; init; } = "GroupSplit123!";

    /// <summary>
    /// Whether an account that already holds a seeded email under a different id is replaced.
    /// <para>
    /// This is the case that makes the whole thing worth doing: an account registered by hand
    /// has a Keycloak-generated subject, the seeded row in the app database carries the seed's
    /// own id, and nothing links the two -- so every request provisions again, collides on the
    /// unique email index, and fails. Replacing the account is what puts the two ends back in
    /// agreement, and it is safe only because these are local demo accounts the seeder owns.
    /// Turn it off to leave such an account alone and be told about it instead.
    /// </para>
    /// </summary>
    public bool ReplaceConflictingUsers { get; init; } = true;

}

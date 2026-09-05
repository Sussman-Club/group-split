namespace GroupSplit.Seeder.Seeders.DTOs;

public class UserSeedDto
{
    public required Guid Id { get; set; }

    /// <summary>
    /// The account's id in Keycloak, and the identity id the app row is linked by. One value
    /// for both ends on purpose: <see cref="KeycloakUserSeeder"/> creates the Keycloak account
    /// with this id, so the subject in the token matches the row the database was seeded with.
    /// </summary>
    public required string ExternalUserId { get; set; }

    public required string FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }

    /// <summary>
    /// The password for the seeded Keycloak account. Falls back to
    /// <see cref="Options.KeycloakSeedOptions.DefaultPassword"/>, which is what every demo
    /// account uses; set it here only for an account that needs its own.
    /// </summary>
    public string? Password { get; set; }

    public IReadOnlyCollection<Guid> GroupIds { get; set; } = [];
}

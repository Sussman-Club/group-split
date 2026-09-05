using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Keycloak;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Creates the demo accounts in Keycloak from the same <c>users.json</c> the app database is
/// seeded from, each with the seed's own <see cref="UserSeedDto.ExternalUserId"/> as its
/// Keycloak id.
/// </summary>
/// <remarks>
/// <para>
/// That shared id is the point. The API links an account by the token's subject, and creates
/// one on first sight of an unknown subject. Without this, a demo account registered by hand
/// gets a Keycloak-generated subject that matches no seeded row, so the API tries to create a
/// second account with the same email and every request dies on the unique email index.
/// </para>
/// <para>
/// Deliberately not in <c>realms.json</c>: that file ships to production, and realm import
/// only runs when the realm does not exist yet, so users put there would neither stay
/// local nor reach a realm that already exists.
/// </para>
/// <para>
/// Independent of the database seeders -- it reads the same file but writes somewhere else --
/// so it carries no <see cref="DependsOnAttribute"/> and runs alongside them.
/// </para>
/// </remarks>
public sealed class KeycloakUserSeeder(
    IKeycloakAdminClient keycloak,
    ISeedDataSource<UserSeedDto> source,
    IOptions<KeycloakSeedOptions> options,
    ILogger<KeycloakUserSeeder> logger) : ISeeder
{
    private readonly KeycloakSeedOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            // Every other seeder needs only the database. This one needs an identity provider
            // and credentials for it, so a run without them seeds what it can and says so.
            logger.LogInformation(
                "No Keycloak admin credentials configured; skipping the realm's demo accounts.");
            return;
        }

        await keycloak.SignInAsync(ct);

        var created = 0;
        var skipped = 0;

        await foreach (var dto in source.ReadAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(dto.Email))
            {
                logger.LogWarning(
                    "Seed user {ExternalUserId} has no email; a Keycloak account needs one, so it is skipped.",
                    dto.ExternalUserId);
                skipped++;
                continue;
            }

            if (await SeedUserAsync(dto, ct))
                created++;
            else
                skipped++;
        }

        logger.LogInformation(
            "Seeded {Created} Keycloak accounts in realm {Realm}, {Skipped} left as they were.",
            created, keycloak.Realm, skipped);
    }

    /// <returns>Whether an account was created.</returns>
    private async Task<bool> SeedUserAsync(UserSeedDto dto, CancellationToken ct)
    {
        // Already seeded: the id is the one the app database points at, so there is nothing
        // to reconcile whatever else has changed about the account.
        if (await keycloak.FindByIdAsync(dto.ExternalUserId, ct) is { } existing)
        {
            logger.LogDebug("Keycloak already has {Email} under the seeded id {Id}.", dto.Email, existing.Id);
            return false;
        }

        if (await keycloak.FindByEmailAsync(dto.Email!, ct) is { } conflicting)
        {
            if (!_options.ReplaceConflictingUsers)
            {
                logger.LogWarning(
                    "Keycloak already has {Email} under id {ActualId}, but the seed data expects {SeededId}. "
                    + "Signing in as that account will fail against the seeded data. Delete it in the Keycloak "
                    + "console, or turn on Keycloak:ReplaceConflictingUsers to have the seeder replace it.",
                    dto.Email, conflicting.Id, dto.ExternalUserId);
                return false;
            }

            // Replaced rather than left alone: an account under the wrong id is exactly the
            // state that makes the app fail, and these are demo accounts the seeder owns.
            logger.LogWarning(
                "Replacing the Keycloak account for {Email}: it has id {ActualId} and the seed data expects "
                + "{SeededId}, which is what the app database is linked to.",
                dto.Email, conflicting.Id, dto.ExternalUserId);

            await keycloak.DeleteAsync(conflicting.Id, ct);
        }

        await keycloak.CreateAsync(new KeycloakUser
        {
            Id = dto.ExternalUserId,
            // The realm registers with the email as the username, so the two match and there
            // is only one thing to type on the login form.
            Username = dto.Email!,
            Email = dto.Email!,
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Credentials =
            [
                new KeycloakCredential { Value = dto.Password ?? _options.DefaultPassword }
            ]
        }, ct);

        logger.LogInformation("Created the Keycloak account for {Email} as {Id}.", dto.Email, dto.ExternalUserId);

        return true;
    }
}

using System.Security.Claims;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Services;

public interface IUserProvisioner
{
    Task<User> GetOrCreate(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

internal sealed class UserProvisioner(AppDbContext context) : IUserProvisioner
{
    public async Task<User> GetOrCreate(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var identityId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(identityId))
        {
            throw new InvalidOperationException("The authenticated user does not have a name identifier claim.");
        }

        var users = context.Set<User>();
        var existingUser = await users.FirstOrDefaultAsync(
            user => user.Identity.IdentityId == identityId,
            cancellationToken);

        if (existingUser is not null)
        {
            // Keycloak owns the profile -- the app only ever reads these, and the account
            // page hands editing off to Keycloak's own console -- so a rename or an address
            // change there has to be mirrored here or the app shows the values captured at
            // first sign-in forever. Written only when something actually differs: this runs
            // on every authenticated request, via CurrentUserMiddleware.
            if (ApplyProfile(existingUser, principal))
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            return existingUser;
        }

        var personalGroup = new Group
        {
            Name = "Personal",
            Rules =
            {
                new Rule
                {
                    Category = Rule.PersonalDefault,
                    Flags = RuleFlags.NonEditable | RuleFlags.NonDeletable,
                    Versions =
                    {
                        new PersonalRuleVersion { StartDateTime = DateTime.UtcNow }
                    }
                }
            }
        };

        var user = new User
        {
            Identity = new UserIdentity { IdentityId = identityId },
            PersonalGroup = personalGroup,
            Groups = { personalGroup }
        };

        ApplyProfile(user, principal);

        users.Add(user);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Provisioning can race on a user's first concurrent requests. At this point
            // no endpoint work has run, so clearing the failed insert graph is safe.
            context.ChangeTracker.Clear();

            var raced = await users.FirstOrDefaultAsync(
                candidate => candidate.Identity.IdentityId == identityId,
                cancellationToken);

            // Only a concurrent provision of this same identity is recoverable, because
            // the row that insert won with is the one this request set out to create.
            // Any other unique violation is a different fault, and treating it as this
            // one buried it: the recovery query found nothing and threw "Sequence
            // contains no elements", naming neither the constraint nor the column.
            if (raced is null)
            {
                throw;
            }

            return raced;
        }
    }

    /// <summary>
    /// Copies the profile claims onto <paramref name="user"/>, reporting whether anything
    /// changed. Shared with the create path so both read the same claims: an absent claim
    /// clears the field, because absent is what Keycloak sends for a value an admin has
    /// cleared.
    /// </summary>
    private static bool ApplyProfile(User user, ClaimsPrincipal principal)
    {
        var firstName = principal.FindFirstValue(ClaimTypes.GivenName);
        var lastName = principal.FindFirstValue(ClaimTypes.Surname);
        var email = principal.FindFirstValue(ClaimTypes.Email);

        if (user.FirstName == firstName && user.LastName == lastName && user.Email == email)
        {
            return false;
        }

        user.FirstName = firstName;
        user.LastName = lastName;
        user.Email = email;

        return true;
    }
}

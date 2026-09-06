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

        // No rule. A personal group used to need one because an expense could only be
        // recorded against a rule, so every account was provisioned with a "Default" rule
        // flagged un-editable and un-deletable to stop anybody breaking it. An expense
        // needs a group and an amount now; a category is optional and a personal one has
        // nobody to divide with anyway.
        var personalGroup = new Group { Name = "Personal" };

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
            return await users.FirstAsync(
                candidate => candidate.Identity.IdentityId == identityId,
                cancellationToken);
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

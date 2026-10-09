using System.Security.Claims;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Services;

public interface IUserProvisioner
{
    Task<User> GetOrCreate(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds the row a token's subject answers to, making it on first sign-in, and keeps its
/// profile in step with Keycloak.
/// </summary>
/// <remarks>
/// The row is keyed on the subject, and <c>User.Email</c> is unique, so the two can
/// disagree: a person deleted and recreated in Keycloak comes back with a new subject and
/// the old address, and the old row still holds it. That sign-in is refused with
/// <see cref="ErrorCodes.IdentityEmailInUse"/> rather than linked to the old row -- an
/// address is not proof of being the person the row belonged to -- and support resolves it
/// by hand (<c>docs/identities.md</c>). An address change that collides is not refused:
/// the person is known by their subject, so they carry on under the address already
/// stored, and the collision is logged under the same code.
/// </remarks>
internal sealed class UserProvisioner(AppDbContext context, ILogger<UserProvisioner> logger) : IUserProvisioner
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

        var email = principal.FindFirstValue(ClaimTypes.Email);

        if (existingUser is not null)
        {
            // A deleted account keeps its subject as a tombstone. Its login is deleted at
            // Keycloak straight after, but that is another system and can lag -- Keycloak can
            // be down, and a token issued before the deletion stays valid until it expires. A
            // sign-in landing in that gap is refused here rather than provisioned: a fresh,
            // empty account under the same login would look to its owner as if the deletion
            // had not worked, and would give the login a reason to exist again.
            if (existingUser.Kind == UserKind.Deleted)
            {
                throw new ForbiddenException(ErrorCodes.AccountDeleted, "This account has been deleted.");
            }

            return await Refresh(existingUser, principal, email, identityId, cancellationToken);
        }

        // A new subject whose address is already on a row. Checked before the insert so the
        // common case reads as what it is rather than as a unique violation; the catch below
        // still covers a holder that arrives in between. A row this same subject made in a
        // concurrent first request is not "another" holder -- that is the race the catch
        // resolves by reading it back.
        if (email is not null
            && await users.AnyAsync(
                user => user.Email == email
                        && (user.Identity == null || user.Identity.IdentityId != identityId),
                cancellationToken))
        {
            throw await EmailInUse(identityId, email, cancellationToken);
        }

        // No personal group either. Provisioning used to make one, because an expense had
        // to belong to a group and to a rule, so every account started with a hidden
        // "Personal" group holding a locked "Default" rule. Both were scaffolding: a
        // personal expense is one with no group, and the group that stood in for it is
        // what put "Personal - 1" in the group switcher and a card on the home page.
        var user = new User
        {
            Identity = new UserIdentity { IdentityId = identityId }
        };

        ApplyProfile(user, principal, email);

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

            if (raced is not null)
            {
                return raced;
            }

            // Not this subject racing itself, so it was the address index that refused: some
            // other row took the address between the check above and the insert.
            if (email is not null)
            {
                throw await EmailInUse(identityId, email, cancellationToken);
            }

            throw;
        }
    }

    /// <summary>
    /// The path every request after the first takes: mirrors the profile onto the row the
    /// subject already has.
    /// </summary>
    /// <remarks>
    /// Keycloak owns the profile -- the app only ever reads these, and the account page
    /// hands editing off to Keycloak's own console -- so a rename or an address change there
    /// has to be mirrored here or the app shows the values captured at first sign-in
    /// forever. Written only when something actually differs: this runs on every
    /// authenticated request, via CurrentUserMiddleware.
    /// <para>
    /// An address another row holds is not taken. The request still goes through, under the
    /// address already stored: who the caller is was settled by the subject, and refusing
    /// would lock them out over a field the app only displays -- for good, when two people
    /// swap addresses, since each would then be waiting on the other. It is logged, and the
    /// next request tries again, so it resolves by itself once the other row lets go.
    /// </para>
    /// </remarks>
    private async Task<User> Refresh(
        User user,
        ClaimsPrincipal principal,
        string? email,
        string identityId,
        CancellationToken cancellationToken)
    {
        if (email is not null
            && email != user.Email
            && await context.Set<User>().AnyAsync(
                other => other.Email == email && other.Id != user.Id,
                cancellationToken))
        {
            LogEmailChangeKept(user.Id, identityId);
            email = user.Email;
        }

        if (!ApplyProfile(user, principal, email))
        {
            return user;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The address was taken between the check and the write. Same answer as above:
            // carry on as the row stands, and try again on the next request. Nothing but
            // this profile write is tracked yet, so dropping it is all clearing does.
            LogEmailChangeKept(user.Id, identityId);
            context.ChangeTracker.Clear();

            return await context.Set<User>().FirstAsync(
                candidate => candidate.Identity.IdentityId == identityId,
                cancellationToken);
        }
    }

    private async Task<ConflictException> EmailInUse(
        string identityId,
        string email,
        CancellationToken cancellationToken)
    {
        // The holder's id goes to the log, for support to act on, and not to the caller:
        // the caller is precisely who has not shown they are entitled to that row.
        var holderId = await context.Set<User>()
            .Where(user => user.Email == email)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        logger.LogWarning(
            "{Code}: sign-in {IdentityId} is new, and its email is already on user {HolderUserId}. Refused until support resolves it.",
            ErrorCodes.IdentityEmailInUse, identityId, holderId);

        return new ConflictException(
            ErrorCodes.IdentityEmailInUse,
            "This sign-in is new here, and its email is already on another account. Support has to resolve it.");
    }

    private void LogEmailChangeKept(Guid userId, string identityId) =>
        logger.LogWarning(
            "{Code}: user {UserId} (sign-in {IdentityId}) has an email in Keycloak that another user holds. Kept the stored email.",
            ErrorCodes.IdentityEmailInUse, userId, identityId);

    /// <summary>
    /// Copies the profile claims onto <paramref name="user"/>, reporting whether anything
    /// changed. Shared with the create path so both read the same claims: an absent claim
    /// clears the field, because absent is what Keycloak sends for a value an admin has
    /// cleared. The address is passed in rather than read, because the caller may have had
    /// to keep the stored one.
    /// </summary>
    private static bool ApplyProfile(User user, ClaimsPrincipal principal, string? email)
    {
        var firstName = principal.FindFirstValue(ClaimTypes.GivenName);
        var lastName = principal.FindFirstValue(ClaimTypes.Surname);

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

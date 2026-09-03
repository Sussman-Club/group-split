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
            FirstName = principal.FindFirstValue(ClaimTypes.GivenName),
            LastName = principal.FindFirstValue(ClaimTypes.Surname),
            Email = principal.FindFirstValue(ClaimTypes.Email),
            Identity = new UserIdentity { IdentityId = identityId },
            PersonalGroup = personalGroup,
            Groups = { personalGroup }
        };

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
}

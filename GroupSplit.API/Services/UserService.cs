using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Npgsql;

namespace GroupSplit.API.Services;

public interface IUserService
{
    ValueTask<User> GetCurrentUser();
}

public class UserService(IHttpContextAccessor httpContextAccessor) : IUserService
{
    public async ValueTask<User> GetCurrentUser()
    {
        if (httpContextAccessor.HttpContext is not
            {
                User: var claimsPrincipal,
                RequestServices: var services,
                RequestAborted: var cancellationToken
            })
        {
            throw new InvalidOperationException("No HttpContext available");
        }

        var claimUserId = claimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        var context = services.GetRequiredService<AppDbContext>();
        var users = context.Set<User>();

        var user = await users
            .FirstOrDefaultAsync(user => user.Identity.IdentityId == claimUserId, cancellationToken);

        if (user is not null)
        {
            return user;
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
                        new PersonalRuleVersion
                        {
                            StartDateTime = DateTime.UtcNow,
                        }
                    }
                }
            }
        };

        user = new User
        {
            Identity = new UserIdentity { IdentityId = claimUserId },
            Email = claimsPrincipal.FindFirstValue(ClaimTypes.Email),
            PersonalGroup = personalGroup, 
            Groups =
            {
                personalGroup
            }
        };

        users.Add(user);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            context.Entry(user).State = EntityState.Detached;

            return await users
                .FirstAsync(u => u.Identity.IdentityId == claimUserId, cancellationToken);
        }

        return user;
    }
}
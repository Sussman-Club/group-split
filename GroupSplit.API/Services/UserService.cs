using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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
            Name = "Personal"
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
        await context.SaveChangesAsync(cancellationToken);

        return user;
    }
}
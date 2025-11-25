using GroupSplit.Data;
using GroupSplit.Data.Entities;

namespace GroupSplit.API.Services;

public interface IGroupService
{
    /// <summary>
    /// Creates a new group for the current user.
    /// </summary>
    ValueTask<Group> CreateGroup(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all groups for the current user.
    /// </summary>
    Task<IQueryable<Group>> GetAllGroups(CancellationToken cancellationToken = default);
}

public class GroupService(IUserService userService, AppDbContext context) : IGroupService
{
    public async ValueTask<Group> CreateGroup(CancellationToken cancellationToken = default)
    {
        var user = await userService.GetCurrentUser();

        var group = new Group
        {
            Users =
            {
                user
            },
            Name = Guid.NewGuid().ToString()
        };

        context.Add(group);
        await context.SaveChangesAsync(cancellationToken);

        return group;
    }

    public async Task<IQueryable<Group>> GetAllGroups(CancellationToken cancellationToken = default)
    {
        var user = await userService.GetCurrentUser();

        // Load the user's groups
        return context.Entry(user).Collection(u => u.Groups).Query();
    }
}
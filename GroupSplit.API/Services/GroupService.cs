using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IGroupService
{
    /// <summary>
    /// Creates a new group for the current user.
    /// </summary>
    ValueTask<Group> CreateGroup(CreateGroupRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all groups for the current user.
    /// </summary>
    Task<IQueryable<Group>> GetAllGroups(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a group by ID for the current user.
    /// </summary>
    ValueTask<Group?> GetGroupById(Guid groupId, CancellationToken cancellationToken = default);
}

public class GroupService(IUserService userService, AppDbContext context) : IGroupService
{
    public async ValueTask<Group> CreateGroup(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userService.GetCurrentUser();

        var group = new Group
        {
            Users =
            {
                user
            },
            Name = request.Name
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

    public async ValueTask<Group?> GetGroupById(Guid groupId, CancellationToken cancellationToken = default)
    {
        var user = await userService.GetCurrentUser();

        return await context.Set<Group>()
            .Where(g => g.Id == groupId && g.Users.Any(u => u.Id == user.Id))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
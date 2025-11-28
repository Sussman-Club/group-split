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
    Task<IQueryable<Group>> GetGroupById(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the members of a group by ID
    /// </summary>
    Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default);


    /// <summary>
    /// Adds a member to a gruopy by ID and emails
    /// </summary>
    Task<IQueryable<Group>> AddGroupMembers(Guid groupId, AddMemberRequest request, CancellationToken cancellationToken = default);



    /// <summary>
    /// Removes a member from a group by ID and user ID
    /// </summary>
    Task<IQueryable<Group>> RemoveGroupMembers(Guid groupId, Guid userId, CancellationToken cancellationToken = default);
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
        return context.Set<Group>()
                .Where(g => g.Users.Any(u => u.Id == user.Id));
    }

    public async Task<IQueryable<Group>> GetGroupById(Guid groupId, CancellationToken cancellationToken = default)
    {
        return from g in await GetAllGroups(cancellationToken)
               where g.Id == groupId
               select g;
    }

    public async Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default)
    {
        return from gr in await GetGroupById(groupId, cancellationToken)
               from user in gr.Users
               select user;
    }

    public async Task<IQueryable<Group>> AddGroupMembers(Guid groupId, AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        var groupQuery = await GetGroupById(groupId, cancellationToken);
        var group = await groupQuery.FirstOrDefaultAsync();
        if (group is null)
            throw new ArgumentException("Group was not found");
        var users = from user in context.Set<User>()
                    where request.UserIdentifiers.Select(ui => ui.Email).Contains(user.Email)
                    select user;
        await foreach (var user in users.AsAsyncEnumerable().WithCancellation(cancellationToken))
            group.Users.Add(user);
        await context.SaveChangesAsync();
        return groupQuery;
    }

    public async Task<IQueryable<Group>> RemoveGroupMembers(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();
        if (userId == currentUser.Id)
            throw new ArgumentException("Cannot remove current user from group");
        var groupQuery = (await GetGroupById(groupId, cancellationToken)).Include(g => g.Users);
        var group = await groupQuery.FirstOrDefaultAsync();
        if (group is null)
            throw new ArgumentException("Group was not found");
        var user = await context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken); ;
        if (user is null)
            throw new ArgumentException("User was not found");
        group.Users.Remove(user);
        await context.SaveChangesAsync(cancellationToken);
        return groupQuery;
    }
}
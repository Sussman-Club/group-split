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

    Task UpdateGroup(Guid groupId, CreateGroupRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the members of a group by ID
    /// </summary>
    Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default);


    /// <summary>
    /// Adds a member to a gruopy by ID and emails
    /// </summary>
    Task AddGroupMembers(Guid groupId, AddMemberRequest request, CancellationToken cancellationToken = default);
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

    public async Task<IQueryable<Group>> GetGroupById(Guid groupId, CancellationToken cancellationToken = default)
    {
        return from g in await GetAllGroups(cancellationToken)
               where g.Id == groupId
               select g;
    }

    public async Task UpdateGroup(Guid groupId, CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var group = await GetGroupById(groupId, cancellationToken);
        
        var existingGroup = await group.FirstOrDefaultAsync(cancellationToken);
        
        if (existingGroup is null)
            throw new Exception("Group was not found");
        
        existingGroup.Name = request.Name;
        
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default)
    {
        return from gr in await GetGroupById(groupId, cancellationToken)
               from user in gr.Users
               select user;
    }

    public async Task AddGroupMembers(Guid groupId, AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        var groupQuery = await GetGroupById(groupId, cancellationToken);
        var group = await groupQuery.FirstOrDefaultAsync();
        if (group is null)
            throw new ArgumentException("Group was not found");
        var users = from user in context.Set<User>()
                    where request.Emails.Contains(user.Email)
                    select user;
        await foreach (var user in users.AsAsyncEnumerable().WithCancellation(cancellationToken))
            group.Users.Add(user);
        await context.SaveChangesAsync();
    }
}
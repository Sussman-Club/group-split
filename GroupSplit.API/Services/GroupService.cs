using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public interface IGroupService
{
    /// <summary>
    /// Creates a new group for the current user.
    /// </summary>
    ValueTask<GroupInfo> CreateGroup(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all groups for the current user.
    /// </summary>
    ValueTask<IEnumerable<GroupInfo>> GetAllGroups(CancellationToken cancellationToken = default);
}

public class GroupService(IUserService userService, AppDbContext context) : IGroupService
{
    public async ValueTask<GroupInfo> CreateGroup(CancellationToken cancellationToken = default)
    {
        var user = await userService.GetCurrentUser();

        var group = new Group
        {
            Users = { user }
        };

        context.Add(group);
        await context.SaveChangesAsync(cancellationToken);
        
        var personalGroupId = user.PersonalGroup.Id;

        return new GroupInfo(group.Id, group.Id == personalGroupId);
    }

    public async ValueTask<IEnumerable<GroupInfo>> GetAllGroups(CancellationToken cancellationToken = default)
    {
        var user = await userService.GetCurrentUser();
        
        // Load the user's groups
        await context.Entry(user).Collection(u => u.Groups).LoadAsync(cancellationToken);
        
        var personalGroupId = user.PersonalGroup.Id;
        
        return user.Groups.Select(g => new GroupInfo(g.Id, g.Id == personalGroupId));
    }
}

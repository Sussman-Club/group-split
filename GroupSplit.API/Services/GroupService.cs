using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

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
    ValueTask<IEnumerable<GroupInfo>> GetAllGroups(CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a group including whether it's the user's personal group.
/// </summary>
public record GroupInfo(Guid Id, bool IsPersonal);

public class GroupService : IGroupService
{
    private readonly IUserService _userService;
    private readonly AppDbContext _context;

    public GroupService(IUserService userService, AppDbContext context)
    {
        _userService = userService;
        _context = context;
    }

    public async ValueTask<Group> CreateGroup(CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetCurrentUser();

        var group = new Group
        {
            Users = { user }
        };

        _context.Add(group);
        await _context.SaveChangesAsync(cancellationToken);

        return group;
    }

    public async ValueTask<IEnumerable<GroupInfo>> GetAllGroups(CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetCurrentUser();
        
        // Load the user's groups
        await _context.Entry(user).Collection(u => u.Groups).LoadAsync(cancellationToken);
        
        var personalGroupId = user.PersonalGroup.Id;
        
        return user.Groups.Select(g => new GroupInfo(g.Id, g.Id == personalGroupId));
    }
}

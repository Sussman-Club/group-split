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

    Task<CreateGroupRequest?> GetUpdateModel(Guid id, CancellationToken ct = default);

    ValueTask<Group> UpdateGroup(Guid groupId, CreateGroupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the members of a group by ID
    /// </summary>
    Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default);


    /// <summary>
    /// Adds a member to a gruopy by ID and emails
    /// </summary>
    Task<IQueryable<Group>> AddGroupMembers(Guid groupId, AddMemberRequest request,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Removes a member from a group by ID and user ID
    /// </summary>
    Task<IQueryable<Group>> RemoveGroupMember(Guid groupId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the balance of a group per user
    /// </summary>
    Task<IQueryable<GroupNetBalance>> GetGroupNetBalance(Guid groupId, CancellationToken cancellationToken = default);

    Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default);
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

    public async Task<CreateGroupRequest?> GetUpdateModel(Guid id, CancellationToken ct = default)
    {
        return await (from t in await GetGroupById(id, ct)
                      select new CreateGroupRequest
                      {
                          Name = t.Name
                      }).FirstOrDefaultAsync(ct);
    }


    public async ValueTask<Group> UpdateGroup(Guid groupId, CreateGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await GetGroupById(groupId, cancellationToken);

        var existingGroup = await group.FirstOrDefaultAsync(cancellationToken);

        if (existingGroup is null)
            throw new Exception("Group was not found");

        existingGroup.Name = request.Name;

        await context.SaveChangesAsync(cancellationToken);

        return existingGroup;
    }

    public async Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default)
    {
        return from gr in await GetGroupById(groupId, cancellationToken)
               from user in gr.Users
               select user;
    }

    public async Task<IQueryable<Group>> AddGroupMembers(Guid groupId, AddMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var groupQuery = await GetGroupById(groupId, cancellationToken);
        var group = await groupQuery.FirstOrDefaultAsync(cancellationToken: cancellationToken);
        if (group is null)
            throw new ArgumentException("Group was not found");
        var users = from user in context.Set<User>()
                    where request.UserIdentifiers.Select(ui => ui.Email).Contains(user.Email)
                    select user;
        await foreach (var user in users.AsAsyncEnumerable().WithCancellation(cancellationToken))
            group.Users.Add(user);

        await context.SaveChangesAsync(cancellationToken);
        return groupQuery;
    }

    public async Task<IQueryable<Group>> RemoveGroupMember(Guid groupId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();
        if (userId == currentUser.Id)
            throw new ArgumentException("Cannot remove current user from group");

        var groupQuery = (await GetGroupById(groupId, cancellationToken)).Include(g => g.Users);
        var group = await groupQuery.FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            throw new ArgumentException("Group was not found");
        var user = await context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
            throw new ArgumentException("User was not found");

        group.Users.Remove(user);

        await context.SaveChangesAsync(cancellationToken);
        return groupQuery;
    }

    public async Task<IQueryable<GroupNetBalance>> GetGroupNetBalance(Guid groupId, CancellationToken cancellationToken = default)
    {
        var groupQuery = await GetGroupById(groupId, cancellationToken);

        var groupBalance =
                    from @group in groupQuery
                    from user in @group.Users
                    select new GroupNetBalance
                    {
                        UserId = user.Id,
                        UserName = user.FirstName + " " + user.LastName,
                        AmountPaid = (from rule in @group.Rules
                                      from ruleVersion in rule.Versions
                                      where !(ruleVersion is PersonalRuleVersion)
                                      from transaction in ruleVersion.Transactions
                                      where transaction.User == user
                                      select transaction.Amount
                                    ).Sum(),
                        AmountOwed = (from rule in @group.Rules
                                      from ruleVersion in rule.Versions
                                      join percentageRuleVersion in context.Set<PercentRuleVersion>() on ruleVersion.Id equals percentageRuleVersion.Id
                                      from percentUser in percentageRuleVersion.RuleUsers
                                      where percentUser.User == user
                                      from transaction in ruleVersion.Transactions
                                      let raw = transaction.Amount * (decimal)percentUser.Percentage
                                      select 
                                           percentUser.User == transaction.User
                                                  ? transaction.Amount - (from otherUser in percentageRuleVersion.RuleUsers 
                                                      where otherUser != percentUser
                                                      select Math.Floor(transaction.Amount * (decimal)otherUser.Percentage) / 100).Sum()
                                                  : (transaction.Amount > 0 ? Math.Floor(raw) : Math.Ceiling(raw)) / 100
                                    ).Sum()
                    } into balance
                    select new GroupNetBalance
                    {
                        UserId = balance.UserId,
                        UserName = balance.UserName,
                        AmountPaid = Math.Round(balance.AmountPaid, 2),
                        AmountOwed = Math.Round(balance.AmountOwed, 2),
                        Balance = Math.Round(balance.AmountPaid - balance.AmountOwed, 2)
                    };

        return groupBalance;
    }

    public async Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var groupQuery = from @group in await GetGroupById(groupId, cancellationToken)
            from groupUser in (from groupUser in @group.Users
                where groupUser.Id == request.UserId
                select groupUser).DefaultIfEmpty()
            from rule in (from rule in @group.Rules
                where rule.Category == Rule.Settlement
                select rule).DefaultIfEmpty()
            from otherRuleVersion in (from version in context.Set<SettlementRuleVersion>()
                where version.Rule == rule && version.OtherUser == groupUser
                select version).Take(1).DefaultIfEmpty()
            from currentUserRuleVersion in (from version in context.Set<SettlementRuleVersion>()
                where version.Rule == rule && version.OtherUser == currentUser
                select version).Take(1).DefaultIfEmpty()
            select new
            {
                Group = @group,
                User = groupUser,
                SettlementRule = rule,
                SettlementRuleVersion = otherRuleVersion,
                CurrentUserRuleVersion = currentUserRuleVersion
            };

        var result = await groupQuery.FirstOrDefaultAsync(cancellationToken);

        if (result is not
            {
                Group: { } resultGroup, User: var user, SettlementRule: var settlementRule,
                SettlementRuleVersion: var settlementRuleVersion,
                CurrentUserRuleVersion: var currentUserSettlementRuleVersion
            })
        {
            throw new ArgumentException("Group was not found");
        }

        if (user is null)
        {
            throw new ArgumentException("User was not found");
        }

        settlementRule ??= new Rule
        {
            Category = Rule.Settlement,
            Group = resultGroup
        };

        settlementRuleVersion ??= new SettlementRuleVersion
        {
            OtherUser = user,
            StartDateTime = DateTime.UtcNow,
            Rule = settlementRule
        };

        currentUserSettlementRuleVersion ??= new SettlementRuleVersion
        {
            OtherUser = currentUser,
            StartDateTime = DateTime.UtcNow,
            Rule = settlementRule
        };

        var dateTime = DateTime.Now;

        var transactionFromOther = new Transaction
        {
            Amount = request.Amount,
            User = user,
            RuleVersion = settlementRuleVersion,
            DateTime = dateTime,
            Name = "Settlement"
        };

        var transactionToOther = new Transaction
        {
            Amount = -request.Amount,
            User = currentUser,
            RuleVersion = currentUserSettlementRuleVersion,
            DateTime = dateTime,
            Name = "Settlement"
        };

        context.Set<Transaction>().AddRange(transactionFromOther, transactionToOther);
        await context.SaveChangesAsync(cancellationToken);
    }
}
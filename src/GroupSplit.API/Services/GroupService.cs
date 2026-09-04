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
    /// Drops <paramref name="user"/> from <paramref name="group"/> and closes the rule
    /// versions that still include them, so later transactions stop splitting with
    /// somebody who has left.
    /// <para>
    /// Deliberately does not save. Callers batch it with their own changes, which is what
    /// lets an account leaving several groups at once apply as one unit instead of
    /// stopping half way through. It also does not check the balance: whether leaving is
    /// allowed at all is the caller's question, and the caller deleting an account has to
    /// ask it of every group up front.
    /// </para>
    /// </summary>
    Task DetachMember(Group group, User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the balance of a group per user
    /// </summary>
    Task<IQueryable<GroupNetBalance>> GetGroupNetBalance(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same balances, for a group <paramref name="memberId"/> belongs to rather than
    /// one the caller belongs to.
    /// <para>
    /// Needed by anything acting on somebody else's behalf. Going through
    /// <see cref="GetGroupNetBalance"/> instead would scope the lookup to the caller's own
    /// groups, so a group they are not in yields no rows at all -- which reads as a
    /// settled balance rather than as an answer that could not be given.
    /// </para>
    /// </summary>
    Task<IQueryable<GroupNetBalance>> GetGroupNetBalanceFor(Guid groupId, Guid memberId,
        CancellationToken cancellationToken = default);

    Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default);
}

public class GroupService(ICurrentUser userContext, AppDbContext context) : IGroupService
{
    public async ValueTask<Group> CreateGroup(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var user = userContext.User;

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
        var user = userContext.User;

        // Load the user's groups
        return GroupsOf(user.Id);
    }

    /// <summary>
    /// The groups one user belongs to. The caller says whose, so this is the one place
    /// that does not assume the answer is about whoever is signed in.
    /// </summary>
    private IQueryable<Group> GroupsOf(Guid userId) =>
        context.Set<Group>()
            .Where(g => g.Users.Any(u => u.Id == userId));

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
        var currentUser = userContext.User;
        if (userId == currentUser.Id)
            throw new ArgumentException("Cannot remove current user from group");

        var groupQuery = (await GetGroupById(groupId, cancellationToken)).Include(g => g.Users);
        var group = await groupQuery.FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            throw new ArgumentException("Group was not found");
        var user = await context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
            throw new ArgumentException("User was not found");

        var groupBalances = await GetGroupNetBalance(groupId, cancellationToken);
        var userBalance = await groupBalances.Where(gb => gb.UserId == userId)
            .Select(x => x.Balance)
            .FirstOrDefaultAsync(cancellationToken);

        if (userBalance is not 0)
            throw new ArgumentException("User must settle before leaving the group");

        await DetachMember(group, user, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        return groupQuery;
    }

    public async Task DetachMember(Group group, User user, CancellationToken cancellationToken = default)
    {
        group.Users.Remove(user);

        var percentVersions = await (
                from ruleVersion in context.Set<PercentRuleVersion>()
                where ruleVersion.Rule.Group == @group &&
                      ruleVersion.EndDateTime == null &&
                      ruleVersion.RuleUsers.Any(ruleUser => ruleUser.User == user)
                select ruleVersion
            )
            .ToListAsync(cancellationToken);

        // Needed as a second query, not a wider one. Set<PercentRuleVersion>() already
        // returns shares versions, since SharesRuleVersion derives from it, but a shares
        // version records its members in SharedRuleUsers rather than RuleUsers, so the
        // query above never sees them. Until this was here, a member removed from a
        // shares rule stayed in it and every later transaction kept splitting them a
        // share -- which for a deleted account would go on distorting what everyone
        // still in the group owes.
        var sharesVersions = await (
                from ruleVersion in context.Set<SharesRuleVersion>()
                where ruleVersion.Rule.Group == @group &&
                      ruleVersion.EndDateTime == null &&
                      ruleVersion.SharedRuleUsers.Any(ruleUser => ruleUser.User == user)
                select ruleVersion
            )
            .ToListAsync(cancellationToken);

        var now = DateTime.Now;

        foreach (var ruleVersion in percentVersions.Concat<RuleVersion>(sharesVersions))
        {
            ruleVersion.EndDateTime = now;
        }
    }

    public async Task<IQueryable<GroupNetBalance>> GetGroupNetBalance(Guid groupId,
        CancellationToken cancellationToken = default)
    {
        return NetBalances(await GetGroupById(groupId, cancellationToken));
    }

    public Task<IQueryable<GroupNetBalance>> GetGroupNetBalanceFor(Guid groupId, Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var groupQuery = GroupsOf(memberId).Where(g => g.Id == groupId);

        return Task.FromResult(NetBalances(groupQuery));
    }

    /// <summary>
    /// Builds the per-member balances over whatever group query it is handed, so the
    /// arithmetic -- and the truncation it depends on -- has one home no matter who is
    /// asking or on whose behalf.
    /// </summary>
    private IQueryable<GroupNetBalance> NetBalances(IQueryable<Group> groupQuery)
    {
        var groupBalance =
                    from @group in groupQuery
                    from user in @group.Users
                    select new GroupNetBalance
                    {
                        UserId = user.Id,
                        UserName = user.FirstName + " " + user.LastName,
                        AmountPaid = Enumerable.Sum((IEnumerable<decimal>)(from rule in @group.Rules
                                from ruleVersion in rule.Versions
                                where !(ruleVersion is PersonalRuleVersion)
                                from transaction in ruleVersion.Transactions
                                where transaction.User == user
                                select transaction.Amount
                            )),
                        AmountOwed = (from rule in @group.Rules
                                      from ruleVersion in rule.Versions
                                      join percentageRuleVersion in context.Set<PercentRuleVersion>() on ruleVersion.Id equals percentageRuleVersion.Id
                                      from percentUser in percentageRuleVersion.RuleUsers
                                      where percentUser.User == user
                                      from transaction in ruleVersion.Transactions
                                      select 
                                           percentUser.User == transaction.User
                                                  ? transaction.Amount - (from otherUser in percentageRuleVersion.RuleUsers 
                                                                          where otherUser != percentUser 
                                                                          select Math.Truncate(transaction.Amount * (decimal)otherUser.Percentage) / 100).Sum()
                                                  : Math.Truncate(transaction.Amount * (decimal)percentUser.Percentage) / 100
                                    ).Sum()
                    } into balance
                    select new GroupNetBalance
                    {
                        UserId = balance.UserId,
                        UserName = balance.UserName,
                        AmountPaid = balance.AmountPaid,
                        AmountOwed = balance.AmountOwed,
                        Balance = balance.AmountPaid - balance.AmountOwed
                    };

        return groupBalance;
    }

    public async Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUser = userContext.User;

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
            Flags = RuleFlags.NonEditable | RuleFlags.NonDeletable | RuleFlags.NoUserTransactions,
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

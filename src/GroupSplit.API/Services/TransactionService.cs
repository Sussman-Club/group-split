using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ITransactionService
{
    Task<IQueryable<Transaction>> List(CancellationToken ct = default);
    Task<IQueryable<Transaction>> Get(Guid id, CancellationToken ct = default);
    ValueTask<Transaction> Create(CreateTransactionRequest request, CancellationToken ct = default);
    Task<UpdateTransactionRequest?> GetUpdateModel(Guid id, CancellationToken ct = default);
    Task<TransactionDetailsResponse?> GetDetails(Guid id, CancellationToken ct = default);
    ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request, CancellationToken ct = default);
    Task Delete(Guid id, CancellationToken ct = default);
}

public class TransactionService(ICurrentUser userContext, AppDbContext dbContext) : ITransactionService
{
    public async Task<IQueryable<Transaction>> List(CancellationToken ct = default)
    {
        var currentUser = userContext.User;

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                    from rule in @group.Rules
                    from version in rule.Versions
                    from transaction in version.Transactions
                    select transaction;

        return query;
    }

    public async Task<IQueryable<Transaction>> Get(Guid id, CancellationToken ct = default)
    {
        var transactions = await List(ct);

        return transactions.Where(t => t.Id == id);
    }

    public async ValueTask<Transaction> Create(CreateTransactionRequest request,
        CancellationToken ct = default)
    {
        var currentUser = userContext.User;
        var paidByUserId = request.PaidByUserId ?? currentUser.Id;

        if (paidByUserId != currentUser.Id && request.RuleVersionId is null)
            throw new Exception("Paid by user must be the current user or a rule version must be specified");

        if (request.RuleVersionId is null && request.GroupId is { } requestedGroupId)
            await RejectGroupTransactionWithoutARule(currentUser, requestedGroupId, ct);

        var groupQuery =
            request.RuleVersionId is null
                ? dbContext.Entry(currentUser).Reference(u => u.PersonalGroup).Query()
                : dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        var result = await (from @group in groupQuery
                            from rule in @group.Rules
                            from version in rule.Versions
                            where (request.RuleVersionId == null && rule.Category == Rule.PersonalDefault) ||
                                  request.RuleVersionId == version.Id
                            select new
                            {
                                Version = version,
                                RuleAllowsUserTransactions = (rule.Flags & RuleFlags.NoUserTransactions) == 0,
                                User = currentUser.Id == paidByUserId
                                    ? currentUser
                                    : (from groupUser in @group.Users
                                       where groupUser.Id == paidByUserId
                                       select groupUser).FirstOrDefault()
                            })
            .FirstOrDefaultAsync(ct);

        if (result is null)
            throw new Exception("Rule version not found");

        if (!result.RuleAllowsUserTransactions)
            throw new Exception("Rule does not allow user transactions");

        if (result.User is null)
            throw new Exception("User is not in the group");

        if (await RuleVersionReferencesRemovedMember(result.Version, ct))
            throw new Exception("Rule version references a member that was removed from the group");

        var transaction = new Transaction
        {
            Amount = request.Amount,
            DateTime = request.DateTime,
            Name = request.Name,
            Description = request.Description,
            RuleVersion = result.Version,
            User = result.User
        };

        dbContext.Add(transaction);
        await dbContext.SaveChangesAsync(ct);

        return transaction;
    }

    private async IAsyncEnumerable<TransactionSplitResponse> GetTransactionSplits(Transaction transaction, CancellationToken ct = default)
    {
        var ruleVersion = transaction.RuleVersion;

        switch (ruleVersion)
        {
            case PercentRuleVersion percentRuleVersion:
            {
                var ruleUsers = await dbContext.Entry(percentRuleVersion)
                    .Collection(rv => rv.RuleUsers)
                    .Query()
                    .Include(ru => ru.User)
                    .ToListAsync(ct);

                foreach (var ru in ruleUsers)
                {
                    yield return new TransactionSplitResponse(
                        $"{ru.User.FirstName} {ru.User.LastName}",
                        ru.User == transaction.User 
                            ? transaction.Amount - (from otherUser in ruleUsers where otherUser != ru select Math.Truncate(transaction.Amount * (decimal)otherUser.Percentage) / 100).Sum() 
                            : Math.Truncate(transaction.Amount * (decimal)ru.Percentage) / 100
                    );
                }
                
                break;
            }
            default:
                yield return new TransactionSplitResponse(
                    $"{transaction.User.FirstName} {transaction.User.LastName}",
                    transaction.Amount
                );
                
                break;
        }
    }

    public async Task<TransactionDetailsResponse?> GetDetails(Guid id, CancellationToken ct = default)
    {
        // Through Get, which is scoped to the caller's groups, rather than over the whole
        // table. Reading straight from the set returned any transaction to any signed-in
        // caller who knew its id — amount, group name, category, who paid and the full
        // per-member split — while every other read here is scoped and List never shows it.
        var transaction = await (await Get(id, ct))
            .Include(t => t.User)
            .Include(t => t.RuleVersion)
            .ThenInclude(rv => rv.Rule)
            .ThenInclude(r => r.Group)
            .FirstOrDefaultAsync(ct);

        if (transaction is null)
            return null;

        var splits = await GetTransactionSplits(transaction, ct).ToListAsync(ct);

        return new TransactionDetailsResponse
        {
            Id = transaction.Id,
            Name = transaction.Name,
            Description = transaction.Description,
            Amount = transaction.Amount,
            DateTime = transaction.DateTime,
            GroupId = transaction.RuleVersion.Rule.Group.Id,
            GroupName = transaction.RuleVersion.Rule.Group.Name,
            PaidByUserId = transaction.User.Id,
            PaidByUserName = $"{transaction.User.FirstName} {transaction.User.LastName}",
            RuleVersionId = transaction.RuleVersion.Id,
            Category = transaction.RuleVersion.Rule.Category,
            Splits = splits
        };
    }

    public async Task<UpdateTransactionRequest?> GetUpdateModel(Guid id, CancellationToken ct = default)
    {
        var transaction = await (await Get(id, ct))
            .Select(t => new UpdateTransactionRequest
            {
                Amount = t.Amount,
                Description = t.Description,
                Name = t.Name,
                DateTime = t.DateTime,
                PaidByUserId = t.User.Id,
                RuleVersionId = t.RuleVersion.Id
            })
            .FirstOrDefaultAsync(ct);

        return transaction;
    }

    public async ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request,
        CancellationToken ct = default)
    {
        var currentUser = userContext.User;
        var userGroups = dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        var query =
            from transaction in await Get(id, ct)
            from ruleVersion in (from userGroup in userGroups
                                 from rule in userGroup.Rules
                                 from ruleVersion in rule.Versions
                                 where ruleVersion.Id == request.RuleVersionId
                                 select ruleVersion).DefaultIfEmpty()
            from payingUser in (from payingUser in dbContext.Set<User>()
                                where payingUser.Id == request.PaidByUserId &&
                                      (from userGroup in userGroups
                                       where (from groupUser in userGroup.Users
                                              where groupUser == payingUser
                                              select 1).Any()
                                       select 1).Any()
                                select payingUser).DefaultIfEmpty()
            select new
            {
                PayingUserBelongsToGroup = payingUser == null ||
                                           (from user in ruleVersion.Rule.Group.Users
                                            where user == payingUser
                                            select 1)
                                           .Any(),
                Transaction = transaction,
                PayingUser = payingUser,
                RuleVersion = ruleVersion,
                RuleAllowsUserTransactions = ruleVersion == transaction.RuleVersion || 
                                             (ruleVersion != null &&
                                              (ruleVersion.Rule.Flags & RuleFlags.NoUserTransactions) == 0 && 
                                              (transaction.RuleVersion.Rule.Flags & RuleFlags.NoUserTransactions) == 0)
            };

        var result = await query.FirstOrDefaultAsync(ct);

        if (result is null) throw new Exception("Transaction not found");

        if (result.RuleVersion is null) throw new Exception("Rule version not found");

        if (result.PayingUser is null) throw new Exception("Paid by user not found");

        if (!result.PayingUserBelongsToGroup) throw new Exception("Paid by user is not in the group");

        if (!result.RuleAllowsUserTransactions) throw new Exception("Rule does not allow user transactions");

        if (await RuleVersionReferencesRemovedMember(result.RuleVersion, ct))
            throw new Exception("Rule version references a member that was removed from the group");

        var updatedTransaction = result.Transaction;

        updatedTransaction.Amount = request.Amount;
        updatedTransaction.DateTime = request.DateTime;
        updatedTransaction.Name = request.Name;
        updatedTransaction.Description = request.Description;
        updatedTransaction.RuleVersion = result.RuleVersion;
        updatedTransaction.User = result.PayingUser;

        await dbContext.SaveChangesAsync(ct);

        return updatedTransaction;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var query = await Get(id, ct);

        var transaction = await query.Include(x => x.RuleVersion).FirstOrDefaultAsync(ct);

        if (transaction is null)
            throw new Exception("Transaction not found");

        if (await RuleVersionReferencesRemovedMember(transaction.RuleVersion, ct))
            throw new Exception("Rule version references a member that was removed from the group");

        dbContext.Remove(transaction);

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Refuses a transaction aimed at a real group that names no rule version.
    /// </summary>
    /// <remarks>
    /// Without a rule version the query below falls back to the personal group's default
    /// rule, which is right for a personal expense and wrong for anything else: a
    /// transaction the member entered against "Trip to Rome" would be saved as a personal
    /// one, under a group they never picked, and nothing would say so. The client can only
    /// offer rules the group actually has, so this is what a group with no rules looks
    /// like by the time it reaches here.
    /// </remarks>
    private async Task RejectGroupTransactionWithoutARule(
        User currentUser,
        Guid groupId,
        CancellationToken ct)
    {
        var personalGroupId = await dbContext.Entry(currentUser)
            .Reference(u => u.PersonalGroup)
            .Query()
            .Select(personalGroup => personalGroup.Id)
            .FirstOrDefaultAsync(ct);

        // The personal group is the fallback, so naming it explicitly is not an error.
        if (groupId == personalGroupId)
            return;

        var groupHasAUsableRule = await dbContext.Entry(currentUser)
            .Collection(u => u.Groups)
            .Query()
            .Where(@group => @group.Id == groupId)
            .SelectMany(@group => @group.Rules)
            .AnyAsync(rule => (rule.Flags & RuleFlags.NoUserTransactions) == 0, ct);

        throw new InvalidOperationException(groupHasAUsableRule
            ? "A rule must be selected for a transaction in this group."
            : "This group has no rule to record a transaction against. Add a rule to the group first.");
    }

    private async Task<bool> RuleVersionReferencesRemovedMember(
        RuleVersion ruleVersion,
        CancellationToken cancellationToken)
    {
        if (ruleVersion is not PercentRuleVersion)
            return false;

        return await dbContext.Set<PercentRuleUser>()
            .AnyAsync(ru =>
                    ru.RuleVersion == ruleVersion &&
                    ru.RuleVersion.Rule.Group.Users.All(gu => gu != ru.User),
                cancellationToken);
    }
}

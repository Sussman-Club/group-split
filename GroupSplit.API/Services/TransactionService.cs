using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ITransactionService
{
    Task<IQueryable<Transaction>> List(CancellationToken cancellationToken = default);

    Task<IQueryable<Transaction>> Get(Guid id, CancellationToken cancellationToken = default);

    ValueTask<Transaction> Create(CreateTransactionRequest request, CancellationToken cancellationToken = default);
    Task<UpdateTransactionRequest?> GetUpdateModel(Guid id, CancellationToken ct = default);

    ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task Delete(Guid id, CancellationToken cancellationToken = default);
}

public class TransactionService(IUserService userService, AppDbContext dbContext) : ITransactionService
{
    public async Task<IQueryable<Transaction>> List(CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            from transaction in version.Transactions
            select transaction;

        return query;
    }

    public async Task<IQueryable<Transaction>> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var transactions = await List(cancellationToken);

        return transactions.Where(t => t.Id == id);
    }

    public async ValueTask<Transaction> Create(CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();
        var paidByUserId = request.PaidByUserId ?? currentUser.Id;

        if (paidByUserId != currentUser.Id && request.RuleVersionId is null)
            throw new Exception("Paid by user must be the current user or a rule version must be specified");

        var groupQuery =
            request.RuleVersionId is null
                ? dbContext.Entry(currentUser).Reference(u => u.PersonalGroup).Query()
                : dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        var ruleVersion = await (from @group in groupQuery
                from rule in @group.Rules
                from version in rule.Versions
                where request.RuleVersionId == null || request.RuleVersionId == version.Id
                select new
                {
                    Version = version,
                    User = currentUser.Id == paidByUserId
                        ? currentUser
                        : (from groupUser in @group.Users
                            where groupUser.Id == paidByUserId
                            select groupUser).FirstOrDefault()
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (ruleVersion is null)
            throw new Exception("Rule version not found");

        if (ruleVersion.User is null)
            throw new Exception("User is not in the group");

        var transaction = new Transaction
        {
            Amount = request.Amount,
            DateTime = request.DateTime,
            Name = request.Name,
            Description = request.Description,
            RuleVersion = ruleVersion.Version,
            User = ruleVersion.User
        };

        dbContext.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        return transaction;
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
        CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();
        var userGroups = dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        var query = from transaction in await Get(id, cancellationToken)
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
                RuleVersion = ruleVersion
            };

        var result = await query.FirstOrDefaultAsync(cancellationToken);

        if (result is null) throw new Exception("Transaction not found");

        if (result.RuleVersion is null) throw new Exception("Rule version not found");

        if (result.PayingUser is null) throw new Exception("Paid by user not found");

        if (!result.PayingUserBelongsToGroup) throw new Exception("Paid by user is not in the group");

        var updatedTransaction = result.Transaction;

        updatedTransaction.Amount = request.Amount;
        updatedTransaction.DateTime = request.DateTime;
        updatedTransaction.Name = request.Name;
        updatedTransaction.Description = request.Description;
        updatedTransaction.RuleVersion = result.RuleVersion;
        updatedTransaction.User = result.PayingUser;

        await dbContext.SaveChangesAsync(cancellationToken);

        return updatedTransaction;
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var query = await Get(id, cancellationToken);
        var transaction = await query.FirstOrDefaultAsync(cancellationToken);
        if (transaction is null)
            throw new Exception("Transaction not found");
        dbContext.Remove(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
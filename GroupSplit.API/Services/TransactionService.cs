using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ITransactionService
{
    Task<IQueryable<Transaction>> GetAll(CancellationToken cancellationToken = default);
    ValueTask<Transaction> Create(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> Delete(Guid id, CancellationToken cancellationToken = default);
}

public class TransactionService(IUserService userService, AppDbContext dbContext) : ITransactionService
{
    public async Task<IQueryable<Transaction>> GetAll(CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();

        var query = from @group in dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            from transaction in version.Transactions
            select transaction;

        return query;
    }

    public async ValueTask<Transaction> Create(CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await userService.GetCurrentUser();
        var paidByUserId = request.PaidByUserId ?? currentUser.Id;

        if (paidByUserId != currentUser.Id && request.RuleVersionId is null)
            throw new Exception("Paid by user must be the current user or a rule version must be specified");

        var groupQuery =
            request.PaidByUserId is null
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
            DateTime = request.Date,
            Name = request.Name,
            Description = request.Description,
            RuleVersion = ruleVersion.Version,
            User = ruleVersion.User
        };

        dbContext.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        return transaction;
    }

    public ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

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
        var user = await userService.GetCurrentUser();

        var query = from @group in dbContext.Entry(user).Collection(u => u.Groups).Query()
            from rule in @group.Rules
            from version in rule.Versions
            from transaction in version.Transactions
            select transaction;

        return query;
    }

    public ValueTask<Transaction> Create(CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
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
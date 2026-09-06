using GroupSplit.Data;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ITransactionService
{
    Task<IQueryable<Expense>> List(CancellationToken ct = default);
    Task<IQueryable<Expense>> Get(Guid id, CancellationToken ct = default);
    ValueTask<Expense> Create(CreateTransactionRequest request, CancellationToken ct = default);
    Task<UpdateTransactionRequest?> GetUpdateModel(Guid id, CancellationToken ct = default);
    Task<TransactionDetailsResponse?> GetDetails(Guid id, CancellationToken ct = default);
    ValueTask<Expense> Update(Guid id, UpdateTransactionRequest request, CancellationToken ct = default);
    Task Delete(Guid id, CancellationToken ct = default);
}

public class TransactionService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    IExpenseSplitter splitter) : ITransactionService
{
    /// <summary>
    /// The caller's expenses, and only their expenses.
    /// </summary>
    /// <remarks>
    /// <c>Set&lt;Expense&gt;()</c> rather than a rule hierarchy walked down to its
    /// transactions. Settlements are absent because a transfer is a different type, not
    /// because anybody remembered to filter them out -- EF puts the discriminator in the
    /// predicate itself.
    /// </remarks>
    public Task<IQueryable<Expense>> List(CancellationToken ct = default)
    {
        var currentUser = userContext.User;

        var groups = dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        var query = from expense in dbContext.Set<Expense>()
                    where groups.Any(@group => @group.Id == expense.GroupId)
                    select expense;

        return Task.FromResult(query);
    }

    public async Task<IQueryable<Expense>> Get(Guid id, CancellationToken ct = default)
    {
        var transactions = await List(ct);

        return transactions.Where(t => t.Id == id);
    }

    /// <summary>
    /// Records an expense against a group, optionally under a category.
    /// </summary>
    /// <remarks>
    /// Everything a transaction needs is here: a group, an amount, a payer. A category is
    /// optional and only says what it was for; when it names a rule the expense is divided
    /// by it, and otherwise evenly. There is no longer such a thing as a group you cannot
    /// record against, which is what four of the error codes this replaces were for.
    /// </remarks>
    public async ValueTask<Expense> Create(CreateTransactionRequest request,
        CancellationToken ct = default)
    {
        var currentUser = userContext.User;
        var paidByUserId = request.PaidByUserId ?? currentUser.Id;

        var group = await GroupFor(request.GroupId, ct);

        var payer = await MemberOf(group, paidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        "The paying user is not a member of the group.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        var expense = new Expense
        {
            Amount = request.Amount,
            Currency = group.Currency,
            DateTime = request.DateTime,
            Name = request.Name,
            Description = request.Description,
            Group = group,
            Category = category,
            User = payer
        };

        await splitter.WriteSplitsAsync(expense, ct);

        dbContext.Add(expense);
        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    public async Task<TransactionDetailsResponse?> GetDetails(Guid id, CancellationToken ct = default)
    {
        // Through Get, which is scoped to the caller's groups, rather than over the whole
        // table. Reading straight from the set returned any transaction to any signed-in
        // caller who knew its id — amount, group name, category, who paid and the full
        // per-member split — while every other read here is scoped and List never shows it.
        var transaction = await (await Get(id, ct))
            .Include(t => t.User)
            .Include(t => t.Group)
            .Include(t => t.Category)
            .Include(t => t.Splits)
            .ThenInclude(split => split.User)
            .FirstOrDefaultAsync(ct);

        if (transaction is null)
            return null;

        // Read, not re-derived. The amounts below are the ones the group's balances are
        // summed from, so a detail view that computed its own could disagree with them.
        var splits = transaction.Splits
            .Select(split => new TransactionSplitResponse(
                $"{split.User.FirstName} {split.User.LastName}",
                split.Amount))
            .ToList();

        return new TransactionDetailsResponse
        {
            Id = transaction.Id,
            Name = transaction.Name,
            Description = transaction.Description,
            Amount = transaction.Amount,
            DateTime = transaction.DateTime,
            GroupId = transaction.Group!.Id,
            GroupName = transaction.Group.Name,
            PaidByUserId = transaction.User.Id,
            PaidByUserName = $"{transaction.User.FirstName} {transaction.User.LastName}",
            CategoryId = transaction.CategoryId,
            Category = transaction.Category?.Name,
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
                CategoryId = t.CategoryId
            })
            .FirstOrDefaultAsync(ct);

        return transaction;
    }

    public async ValueTask<Expense> Update(Guid id, UpdateTransactionRequest request,
        CancellationToken ct = default)
    {
        var expense = await (await Get(id, ct))
            .Include(t => t.Group)
            .Include(t => t.Splits)
            .FirstOrDefaultAsync(ct);

        if (expense is null)
            throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        var group = expense.Group!;

        var payer = await MemberOf(group, request.PaidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        "The paying user is not a member of the group.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        expense.Amount = request.Amount;
        expense.DateTime = request.DateTime;
        expense.Name = request.Name;
        expense.Description = request.Description;
        expense.Category = category;
        expense.CategoryId = category?.Id;
        expense.User = payer;

        // The amount, the payer and the category can all have changed, and each of them
        // changes what everybody owed. Recomputed rather than adjusted, because there is no
        // edit for which keeping the old split would be right.
        await splitter.WriteSplitsAsync(expense, ct);

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var transaction = await (await Get(id, ct)).FirstOrDefaultAsync(ct);

        if (transaction is null)
            throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        dbContext.Remove(transaction);

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The group the expense belongs to: the one named, or the caller's personal group when
    /// none was.
    /// </summary>
    private async Task<Group> GroupFor(Guid? groupId, CancellationToken ct)
    {
        var currentUser = userContext.User;

        if (groupId is null)
        {
            return await dbContext.Entry(currentUser).Reference(u => u.PersonalGroup).Query()
                       .FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
        }

        return await dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                   .FirstOrDefaultAsync(@group => @group.Id == groupId, ct)
               ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
    }

    private Task<User?> MemberOf(Group group, Guid userId, CancellationToken ct) =>
        dbContext.Entry(group).Collection(g => g.Users).Query()
            .FirstOrDefaultAsync(user => user.Id == userId, ct)!;

    /// <summary>
    /// The category, checked to belong to the same group. A category from another group
    /// would file the expense under a label its members cannot see.
    /// </summary>
    private async Task<Category?> CategoryFor(Group group, Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
            return null;

        return await dbContext.Set<Category>()
                   .FirstOrDefaultAsync(category =>
                       category.Id == categoryId && category.Group.Id == group.Id, ct)
               ?? throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");
    }
}

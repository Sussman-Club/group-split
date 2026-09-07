using GroupSplit.Data;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface ITransactionService
{
    /// <summary>
    /// The expenses the caller may read: everything in a group they are in, and everything
    /// they paid for wherever it is -- a personal expense, or one in a group they have since
    /// left. Leaving a flat share does not erase a year of your own spending from your own
    /// records; what it takes away is the right to change it, which is
    /// <see cref="Update"/>'s and <see cref="Delete"/>'s concern.
    /// </summary>
    Task<IQueryable<Expense>> List(CancellationToken ct = default);

    /// <summary>
    /// A group's expenses, for a member of it. Scoped to membership rather than to
    /// <see cref="List"/>, so somebody who has left does not see their own rows through the
    /// group's door: that listing is the group's, not theirs.
    /// </summary>
    Task<IQueryable<Expense>> InGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// The caller's own share rows: one per expense they owe a part of, whoever paid.
    /// </summary>
    /// <remarks>
    /// The listing <see cref="List"/> is not. That one is "what have I paid" -- the rows
    /// where the caller is the payer -- and this is the other half of the same ledger,
    /// which the model has had the rows for since splits became their own table and
    /// nothing has ever read as a list.
    /// <para>
    /// Scoped through <see cref="List"/> rather than straight off the split table, so a
    /// share cannot show an expense the caller may not read; in practice a split against
    /// them always sits on one they may, and going through the same door means it cannot
    /// stop being true here without stopping being true there.
    /// </para>
    /// </remarks>
    Task<IQueryable<TransactionSplit>> Shares(CancellationToken ct = default);

    Task<IQueryable<Expense>> Get(Guid id, CancellationToken ct = default);
    ValueTask<Expense> Create(CreateTransactionRequest request, CancellationToken ct = default);

    /// <summary>
    /// What <paramref name="request"/> would be divided into if it were saved, without
    /// saving it.
    /// </summary>
    /// <remarks>
    /// Runs the same checks and the same splitter the save would, so a preview that comes
    /// back is a preview of what will actually happen and a preview that cannot be given is
    /// the refusal the save would have met -- shown at the step where it can still be
    /// fixed. The dialog could divide evenly itself, and did; a second copy of the money
    /// arithmetic is exactly what the reshape was for getting rid of.
    /// </remarks>
    Task<SplitPreviewResponse> Preview(CreateTransactionRequest request, CancellationToken ct = default);
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

        // Two kinds of expense are the caller's to read: the ones in a group they belong
        // to, and the ones they paid for wherever those are -- personal ones, which have no
        // group, and ones in a group they have since left. The second clause used to be
        // "in no group and mine", which made leaving a group erase everything the leaver
        // had paid in it from their own listing and totals.
        var query = from expense in dbContext.Set<Expense>()
                    where groups.Any(@group => @group.Id == expense.GroupId) ||
                          expense.UserId == currentUser.Id
                    select expense;

        return Task.FromResult(query);
    }

    public async Task<IQueryable<TransactionSplit>> Shares(CancellationToken ct = default)
    {
        var currentUser = userContext.User;
        var expenses = await List(ct);

        // Joined rather than filtered on the navigation, because the join is also the
        // scope: a split whose expense is not in that listing is not returned, and the
        // discriminator on Expense keeps transfers out -- a settlement has a split too,
        // and it is a payment rather than something anybody owes a share of.
        return from split in dbContext.Set<TransactionSplit>()
               join expense in expenses on split.TransactionId equals expense.Id
               where split.UserId == currentUser.Id
               select split;
    }

    public Task<IQueryable<Expense>> InGroup(Guid groupId, CancellationToken ct = default)
    {
        var currentUser = userContext.User;

        var groups = dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        var query = from expense in dbContext.Set<Expense>()
                    where expense.GroupId == groupId && groups.Any(@group => @group.Id == groupId)
                    select expense;

        return Task.FromResult(query);
    }

    /// <summary>
    /// Whether the caller is in the expense's group now. Reading is broader than this --
    /// see <see cref="List"/> -- but a change moves balances for people whose group the
    /// caller may have left, and that is theirs to refuse.
    /// </summary>
    private async Task RefuseIfLeft(Expense expense, CancellationToken ct)
    {
        if (expense.GroupId is not { } groupId)
            return;

        var stillIn = await dbContext.Entry(userContext.User).Collection(u => u.Groups).Query()
            .AnyAsync(@group => @group.Id == groupId, ct);

        if (!stillIn)
            throw new ConflictException(ErrorCodes.TransactionGroupLeft,
                "You are no longer in this expense's group, so it cannot be changed.");
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
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is not a member of the group.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        var expense = new Expense
        {
            Amount = request.Amount,
            Currency = group?.Currency ?? Currencies.Default,
            // Stored as UTC, whatever offset the client wrote it with. The column has no
            // zone, so this only makes explicit what the store does anyway -- and it means
            // an instant reads back exactly as it was sent, never re-expressed.
            DateTime = request.DateTime.ToUniversalTime(),
            Name = request.Name,
            Description = request.Description,
            Group = group,
            Category = category,
            User = payer
        };

        await splitter.WriteSplitsAsync(expense, request.Splits, ct);

        dbContext.Add(expense);
        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    public async Task<SplitPreviewResponse> Preview(CreateTransactionRequest request,
        CancellationToken ct = default)
    {
        var currentUser = userContext.User;
        var paidByUserId = request.PaidByUserId ?? currentUser.Id;

        var group = await GroupFor(request.GroupId, ct);

        var payer = await MemberOf(group, paidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is not a member of the group.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        // Ids only, and never added to the context: the splitter reads the ids when the
        // navigations are absent, so nothing here is reachable from a tracked entity and
        // there is no save on this path to reach it with.
        var draft = new Expense
        {
            Amount = request.Amount,
            Currency = group?.Currency ?? Currencies.Default,
            DateTime = request.DateTime.ToUniversalTime(),
            Name = request.Name,
            GroupId = group?.Id,
            CategoryId = category?.Id,
            UserId = payer.Id
        };

        await splitter.WriteSplitsAsync(draft, request.Splits, ct);

        var named = draft.Splits.Select(split => split.UserId).ToList();

        var names = await dbContext.Set<User>()
            .Where(user => named.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => $"{user.FirstName} {user.LastName}".Trim(), ct);

        var splits = draft.Splits
            .Select(split => new TransactionSplitResponse(
                split.UserId,
                names.GetValueOrDefault(split.UserId, string.Empty),
                split.Amount))
            .ToList();

        return new SplitPreviewResponse(splits, await RuleNameFor(category, ct));
    }

    /// <summary>
    /// The rule the category points at, by name, so the preview can say why the numbers
    /// came out the way they did. Null when the division was even.
    /// </summary>
    private async Task<string?> RuleNameFor(Category? category, CancellationToken ct)
    {
        if (category?.DefaultSplitRuleId is not { } ruleId)
            return null;

        return await dbContext.Set<SplitRule>()
            .Where(rule => rule.Id == ruleId)
            .Select(rule => rule.Name)
            .FirstOrDefaultAsync(ct);
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
                split.User.Id,
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
            GroupId = transaction.GroupId,
            GroupName = transaction.Group?.Name,
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
                GroupId = t.GroupId,
                CategoryId = t.CategoryId
                // Splits are deliberately absent. Null means "divide it again", and that is
                // the only safe default for a model somebody is about to change the amount
                // on: filled in here, an ordinary read-change-write would quietly mean
                // "keep these exact shares" and fail the moment the amount moved. The one
                // caller that needs them -- a patch addressing a share by index, which
                // needs an index to address -- fills them in itself.
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

        await RefuseIfLeft(expense, ct);
        await RefuseIfSettled(expense, ct);

        // The destination, which is the group it is already in unless the request moves it.
        // Resolved the way a create resolves its group -- one of the caller's, or none for
        // personal -- so moving into a group the caller is not in is the same 404 as
        // creating there would be. The old shares are not carried over: they name the old
        // group's members, and the splitter below divides afresh among the new one's.
        var group = request.GroupId == expense.GroupId
            ? expense.Group
            : await GroupFor(request.GroupId, ct);

        if (group?.Id != expense.GroupId)
        {
            expense.Group = group;
            expense.GroupId = group?.Id;
            expense.Currency = group?.Currency ?? Currencies.Default;
        }

        var payer = await MemberOf(group, request.PaidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is not a member of the group.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        expense.Amount = request.Amount;
        expense.DateTime = request.DateTime.ToUniversalTime();
        expense.Name = request.Name;
        expense.Description = request.Description;
        expense.Category = category;
        expense.CategoryId = category?.Id;
        expense.User = payer;

        // The amount, the payer and the category can all have changed, and each of them
        // changes what everybody owed. Recomputed rather than adjusted, because there is no
        // edit for which keeping the old split would be right -- unless the caller stated
        // the division itself, which is the one case where keeping it is the whole point.
        await splitter.WriteSplitsAsync(expense, request.Splits, ct);

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var transaction = await (await Get(id, ct)).FirstOrDefaultAsync(ct);

        if (transaction is null)
            throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        await RefuseIfLeft(transaction, ct);
        await RefuseIfSettled(transaction, ct);

        dbContext.Remove(transaction);

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Refuses to touch a transaction a settling-up has already settled.
    /// </summary>
    /// <remarks>
    /// A run says a set of transactions nets to zero and records the payments that made it
    /// so. Change one of them behind its back and that stops being true in the one way this
    /// model can go quietly wrong: the balances would show the difference -- they read every
    /// row, settled or not -- while settling up said there was nothing left to settle,
    /// because everything that could have paid it off was already marked done.
    /// <para>
    /// Undoing the settling-up first is the answer, and it is a decision rather than a side
    /// effect: reopening a month puts every payment in it back to outstanding, and that is
    /// far too large a thing to happen quietly because somebody corrected a hotel bill. The
    /// run is named in the problem's extensions so the client can offer to undo the one it
    /// means instead of sending anybody looking.
    /// </para>
    /// </remarks>
    private async Task RefuseIfSettled(Transaction transaction, CancellationToken ct)
    {
        if (transaction.SettlementRunId is not { } runId)
            return;

        var run = await dbContext.Set<SettlementRun>()
            .Where(candidate => candidate.Id == runId)
            .Select(candidate => new { candidate.Id, candidate.Label })
            .FirstOrDefaultAsync(ct);

        if (run is null)
            return;

        throw new ConflictException(ErrorCodes.TransactionSettled,
                $"This was settled in \"{run.Label}\". Undo that settling-up to change it.")
            .WithExtension("settlementRunId", run.Id)
            .WithExtension("settlementRunLabel", run.Label);
    }

    /// <summary>
    /// The group the expense belongs to, or null when it belongs to none -- which is what
    /// a personal expense is, rather than a hidden group of one.
    /// </summary>
    private async Task<Group?> GroupFor(Guid? groupId, CancellationToken ct)
    {
        if (groupId is null)
            return null;

        var currentUser = userContext.User;

        return await dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                   .FirstOrDefaultAsync(@group => @group.Id == groupId, ct)
               ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
    }

    /// <summary>
    /// The member of <paramref name="group"/> with that id, or -- when there is no group --
    /// the caller themselves, since a personal expense is one only they can have paid.
    /// </summary>
    private Task<User?> MemberOf(Group? group, Guid userId, CancellationToken ct)
    {
        if (group is null)
        {
            var currentUser = userContext.User;
            return Task.FromResult(userId == currentUser.Id ? currentUser : null);
        }

        return dbContext.Entry(group).Collection(g => g.Users).Query()
            .FirstOrDefaultAsync(user => user.Id == userId, ct)!;
    }

    /// <summary>
    /// The category, checked to belong to the same group. A category from another group
    /// would file the expense under a label its members cannot see.
    /// </summary>
    private async Task<Category?> CategoryFor(Group? group, Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
            return null;

        // Categories belong to groups, so an expense in no group can be filed under none.
        if (group is null)
            throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");

        return await dbContext.Set<Category>()
                   .FirstOrDefaultAsync(category =>
                       category.Id == categoryId && category.Group.Id == group.Id, ct)
               ?? throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");
    }
}

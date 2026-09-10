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
    ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Removes a transaction of either kind: an expense, or a settlement between two
    /// members.
    /// </summary>
    /// <remarks>
    /// The one member of this interface that is not expense-only. Everything else here
    /// reads <c>Set&lt;Expense&gt;()</c> on purpose, so a transfer is invisible to it; a
    /// settlement entered wrongly still has to be retractable, and there is no second
    /// delete path for it to use.
    /// </remarks>
    /// <exception cref="NotFoundException">
    /// No transaction of any kind with that id is the caller's to remove.
    /// </exception>
    /// <exception cref="ConflictException">
    /// The caller has left the group it belongs to.
    /// </exception>
    Task Delete(Guid id, CancellationToken ct = default);
}

public class TransactionService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    IExpenseSplitter splitter,
    IGroupParticipants participants) : ITransactionService
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
    /// Whether the caller is in the transaction's group now. Reading is broader than this --
    /// see <see cref="List"/> -- but a change moves balances for people whose group the
    /// caller may have left, and that is theirs to refuse.
    /// </summary>
    /// <remarks>
    /// Takes a <see cref="Transaction"/> rather than an <see cref="Expense"/> because
    /// <see cref="Delete"/> now reaches it with either kind. The check itself never read
    /// anything expense-shaped: a group is a group, and a settlement recorded in one moves
    /// its balances exactly as much as spending does.
    /// </remarks>
    private async Task RefuseIfLeft(Transaction transaction, CancellationToken ct)
    {
        if (transaction.GroupId is not { } groupId)
            return;

        var stillIn = await dbContext.Entry(userContext.User).Collection(u => u.Groups).Query()
            .AnyAsync(@group => @group.Id == groupId, ct);

        if (!stillIn)
            throw new ConflictException(ErrorCodes.TransactionGroupLeft,
                "You are no longer in this transaction's group, so it cannot be changed.");
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

        var payer = await ParticipantOf(group, paidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is neither a member of the group nor invited to it.");

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
            MerchantId = await MerchantFor(request.MerchantId, ct),
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

        var payer = await ParticipantOf(group, paidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is neither a member of the group nor invited to it.");

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
            .ToDictionaryAsync(user => user.Id, People.Display, ct);

        var waiting = await PendingInviteesIn(group?.Id, ct);

        var splits = draft.Splits
            .Select(split => new TransactionSplitResponse(
                split.UserId,
                names.GetValueOrDefault(split.UserId, string.Empty),
                split.Amount,
                waiting.Contains(split.UserId)))
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
        // Through ScopedTransaction, which is scoped to the caller's groups, rather than over the whole
        // table. Reading straight from the set returned any transaction to any signed-in
        // caller who knew its id — amount, group name, category, who paid and the full
        // per-member split — while every other read here is scoped and List never shows it.
        var transaction = await ScopedTransaction(id)
            .Include(t => t.User)
            .Include(t => t.Group)
            .Include(t => (t as Expense)!.Category)
            .Include(t => t.Merchant)
            .Include(t => t.Splits)
            .ThenInclude(split => split.User)
            .FirstOrDefaultAsync(ct);

        if (transaction is null)
            return null;

        // Who among the people on this transaction has not joined the group yet. One query
        // for the whole transaction rather than one per share, and empty for a personal
        // expense, which has no group to be invited to.
        var waiting = await PendingInviteesIn(transaction.GroupId, ct);

        // Read, not re-derived. The amounts below are the ones the group's balances are
        // summed from, so a detail view that computed its own could disagree with them.
        var splits = transaction.Splits
            .Select(split => new TransactionSplitResponse(
                split.User.Id,
                People.Display(split.User),
                split.Amount,
                waiting.Contains(split.User.Id)))
            .ToList();

        var expense = transaction as Expense;
        var transfer = transaction as Transfer;
        var recipientSplit = transfer != null ? splits.FirstOrDefault() : null;

        return new TransactionDetailsResponse
        {
            Id = transaction.Id,
            Kind = transfer != null ? ActivityKind.Transfer : ActivityKind.Expense,
            Name = transaction.Name,
            Description = transaction.Description,
            Amount = transaction.Amount,
            DateTime = transaction.DateTime,
            GroupId = transaction.GroupId,
            GroupName = transaction.Group?.Name,
            PaidByUserId = transaction.User.Id,
            PaidByUserName = People.Display(transaction.User),
            PaidByIsPendingInvitee = waiting.Contains(transaction.User.Id),
            PaidToUserId = recipientSplit?.UserId,
            PaidToUserName = recipientSplit?.UserName,
            CategoryId = expense?.CategoryId,
            Category = expense?.Category?.Name,
            MerchantName = transaction.Merchant?.Name,
            MerchantLogoUrl = transaction.Merchant?.LogoUrl,
            Splits = splits
        };
    }

    public async Task<UpdateTransactionRequest?> GetUpdateModel(Guid id, CancellationToken ct = default)
    {
        var transaction = await ScopedTransaction(id)
            .Include(t => t.User)
            .Include(t => t.Splits)
            .FirstOrDefaultAsync(ct);

        if (transaction is null)
            return null;

        var expense = transaction as Expense;

        return new UpdateTransactionRequest
        {
            Amount = transaction.Amount,
            Description = transaction.Description,
            Name = transaction.Name,
            DateTime = transaction.DateTime,
            PaidByUserId = transaction.User.Id,
            GroupId = transaction.GroupId,
            CategoryId = expense?.CategoryId,
            MerchantId = transaction.MerchantId,
            Splits = transaction.GroupId == null
                ? null
                : transaction.Splits.Select(s => new SplitInput { UserId = s.UserId, Amount = s.Amount }).ToList()
        };
    }

    public async ValueTask<Transaction> Update(Guid id, UpdateTransactionRequest request,
        CancellationToken ct = default)
    {
        var transaction = await ScopedTransaction(id)
            .Include(t => t.Group)
            .Include(t => t.Splits)
            .FirstOrDefaultAsync(ct);

        if (transaction is null)
            throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        await RefuseIfLeft(transaction, ct);

        if (transaction is Transfer transfer)
        {
            // Members on both ends, unlike the expense below. A settlement is money that
            // actually changed hands between two people, and somebody who has not accepted
            // their invitation has no account to have handed it to -- see
            // RefuseIfPendingInvitee, which says so rather than reporting them missing.
            var payer = await MemberOf(transfer.Group, request.PaidByUserId, ct)
                        ?? await RefuseIfPendingInvitee(transfer.Group, request.PaidByUserId, ct)
                        ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                            "The paying user is not a member of the group.");

            transfer.Amount = request.Amount;
            transfer.DateTime = request.DateTime.ToUniversalTime();
            transfer.Description = request.Description;
            transfer.User = payer;

            if (request.Splits is { Count: > 0 } splits)
            {
                var recipientId = splits[0].UserId;
                var recipient = await MemberOf(transfer.Group, recipientId, ct)
                                ?? await RefuseIfPendingInvitee(transfer.Group, recipientId, ct)
                                ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                                    "The recipient is not a member of the group.");

                if (recipient.Id == payer.Id)
                    throw new ConflictException(ErrorCodes.SettlementWithSelf,
                        "A settlement needs two different people.");

                var existingSplit = transfer.Splits.FirstOrDefault();
                if (existingSplit is not null)
                {
                    existingSplit.User = recipient;
                    existingSplit.UserId = recipient.Id;
                    existingSplit.Amount = request.Amount;
                }
                else
                {
                    transfer.Splits.Add(new TransactionSplit { User = recipient, Amount = request.Amount });
                }
            }
            else
            {
                foreach (var split in transfer.Splits)
                {
                    split.Amount = request.Amount;
                }
            }

            await dbContext.SaveChangesAsync(ct);
            return transfer;
        }

        var expense = (Expense)transaction;

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
            if (group == null)
            {
                request.Splits = null;
            }
        }

        var expensePayer = await ParticipantOf(group, request.PaidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is neither a member of the group nor invited to it.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        expense.Amount = request.Amount;
        expense.DateTime = request.DateTime.ToUniversalTime();
        expense.Name = request.Name;
        expense.Description = request.Description;
        expense.Category = category;
        expense.CategoryId = category?.Id;
        expense.MerchantId = await MerchantFor(request.MerchantId, ct);
        expense.User = expensePayer;

        // The amount, the payer and the category can all have changed, and each of them
        // changes what everybody owed. Recomputed rather than adjusted, because there is no
        // edit for which keeping the old split would be right -- unless the caller stated
        // the division itself, which is the one case where keeping it is the whole point.
        await splitter.WriteSplitsAsync(expense, request.Splits, ct);

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    /// <summary>
    /// The transaction <paramref name="id"/> names, of either kind (expense or settlement),
    /// when it is the caller's to view or modify.
    /// </summary>
    /// <remarks>
    /// The query here that reads <c>Set&lt;Transaction&gt;()</c> rather than
    /// <c>Set&lt;Expense&gt;()</c>. Keeping the reading surfaces expense-only is the point
    /// of the reshape -- a repayment is not spending and does not belong in a list of it --
    /// but deleting/editing is not reading, and routing this through <see cref="Get"/> put the
    /// discriminator in the predicate, so a settlement recorded by mistake answered
    /// "Transaction not found." and went on moving balances until somebody edited the
    /// database. <see cref="Transfer"/> has claimed since it was written that a transfer
    /// "goes through the same splits, the same balance query and the same delete path as
    /// everything else"; the first two were true.
    /// <para>
    /// The scope is <see cref="List"/>'s, so what widens is which kinds of row are in
    /// reach and never whose: a transaction in one of the caller's groups, or one they paid
    /// for wherever it is. Both parties to a transfer are members of its group, so each of
    /// them finds it here. <see cref="RefuseIfLeft"/> still has the final word.
    /// </para>
    /// </remarks>
    private IQueryable<Transaction> ScopedTransaction(Guid id)
    {
        var currentUser = userContext.User;

        var groups = dbContext.Entry(currentUser).Collection(u => u.Groups).Query();

        return from transaction in dbContext.Set<Transaction>()
               where transaction.Id == id &&
                     (groups.Any(@group => @group.Id == transaction.GroupId) ||
                      transaction.UserId == currentUser.Id)
               select transaction;
    }

    public async Task Delete(Guid id, CancellationToken ct = default)
    {
        var transaction = await ScopedTransaction(id).FirstOrDefaultAsync(ct);

        if (transaction is null)
            throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        await RefuseIfLeft(transaction, ct);

        dbContext.Remove(transaction);

        await dbContext.SaveChangesAsync(ct);
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
    /// Which of a group's participants have been invited and have not joined, as ids.
    /// Empty for no group at all, which is what a personal expense has.
    /// </summary>
    private async Task<HashSet<Guid>> PendingInviteesIn(Guid? groupId, CancellationToken ct)
    {
        if (groupId is not { } id)
            return [];

        return [.. await dbContext.Set<GroupInvitation>()
            .Where(invitation => invitation.GroupId == id)
            .Select(invitation => invitation.ParticipantUserId)
            .ToListAsync(ct)];
    }

    /// <summary>
    /// The participant of <paramref name="group"/> with that id: a member, or somebody the
    /// group has invited and is waiting on. When there is no group, the caller themselves,
    /// since a personal expense is one only they can have paid.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="MemberOf"/> by exactly one thing, and only expenses use it.
    /// The trip is booked and the flat is moved into before everybody has answered their
    /// invitation, so an expense may be paid for by an invitee and divided with them; a
    /// settlement may not, because there is no account on the other end of it yet.
    /// </remarks>
    private Task<User?> ParticipantOf(Group? group, Guid userId, CancellationToken ct)
    {
        if (group is null)
        {
            var currentUser = userContext.User;
            return Task.FromResult(userId == currentUser.Id ? currentUser : null);
        }

        return participants.Find(group.Id, userId, ct);
    }

    /// <summary>
    /// Says what is actually wrong when a settlement names somebody the group has invited
    /// and is still waiting on, rather than letting them read as a stranger.
    /// </summary>
    /// <remarks>
    /// Returns null when they are not one, so it composes as a second guess after
    /// <see cref="MemberOf"/> and leaves the original refusal to the caller. It never
    /// returns a user: there is nothing here it would be right to go on and record.
    /// </remarks>
    private async Task<User?> RefuseIfPendingInvitee(Group? group, Guid userId, CancellationToken ct)
    {
        if (group is not null && await participants.IsPendingInvitee(group.Id, userId, ct))
            throw new ConflictException(ErrorCodes.SettlementWithPendingInvitee,
                "That person has been invited to the group and has not joined yet, so there is " +
                "nobody to settle up with. Their balance stands until they accept.");

        return null;
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

    /// <summary>
    /// The shop an expense says it was spent at, checked to exist. Unscoped, unlike the
    /// category above: a merchant belongs to nobody, so there is no group for it to be in
    /// or out of.
    /// </summary>
    private async Task<Guid?> MerchantFor(Guid? merchantId, CancellationToken ct)
    {
        if (merchantId is null)
            return null;

        var exists = await dbContext.Set<Merchant>().AnyAsync(merchant => merchant.Id == merchantId, ct);

        if (!exists)
            throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");

        return merchantId;
    }
}

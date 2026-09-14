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

    /// <summary>
    /// What an edit to an existing expense would divide it into, without saving it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Preview"/> with the expense's current values: an edit re-divides by
    /// the version of the rule the expense was written under, and only a preview that
    /// starts from the stored version can show the numbers the save will actually produce.
    /// <para>
    /// Expenses only. A settlement is not divided, so there is nothing here to show for
    /// one, and it answers as a missing transaction does -- the same answer every other
    /// expense-only surface gives it.
    /// </para>
    /// </remarks>
    /// <param name="redivide">
    /// True to show what dividing it again by its rule would come to, rather than what
    /// <c>PATCH</c> does with an edit that says nothing about the shares -- which is keep
    /// them. False is the default because it is what silence means, and the preview's whole
    /// job is to reproduce the save.
    /// <para>
    /// It exists because the edit dialog has a control that means exactly this, and the
    /// request body cannot carry it: an <see cref="UpdateTransactionRequest"/> with no
    /// shares is the save contract for "keep them", and giving it a second way to say
    /// something else would put the instruction that caused the 2026-09-08 incident back
    /// inside the model the save is built from.
    /// </para>
    /// </param>
    /// <exception cref="NotFoundException">
    /// No expense with that id is the caller's to read, or the id names a settlement.
    /// </exception>
    Task<SplitPreviewResponse> PreviewUpdate(Guid id, UpdateTransactionRequest request,
        bool redivide = false, CancellationToken ct = default);
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
            // The division it was told to use, which the splitter then keeps rather than
            // reaching for the category's. Null is the ordinary expense.
            SplitRuleVersionId = await VersionOfRuleFor(group, request.SplitRuleId, request.Splits, ct),
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
            SplitRuleVersionId = await VersionOfRuleFor(group, request.SplitRuleId, request.Splits, ct),
            UserId = payer.Id
        };

        await splitter.WriteSplitsAsync(draft, request.Splits, ct);

        return await Describe(draft, group?.Id, ct);
    }

    /// <summary>
    /// What an edit would be divided into, for an expense that already exists.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Preview"/> for one reason, and it is the reason the edit
    /// dialog could not simply borrow that one: an edit is divided again by the version of
    /// the rule the expense was <em>written under</em>, and a draft built from scratch has
    /// no such version, so it would be shown today's rule and the save would then produce
    /// different numbers. Carrying the version across is the whole of the difference.
    /// <para>
    /// Everything else is <see cref="Update"/>'s own resolution, in the same order, so a
    /// preview that refuses is the refusal the save would have met -- shown at the step
    /// where there is still a field on screen to fix it in.
    /// </para>
    /// </remarks>
    public async Task<SplitPreviewResponse> PreviewUpdate(Guid id, UpdateTransactionRequest request,
        bool redivide = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Expenses only, like every other surface that means "expenses". A transfer is one
        // split to the recipient and has no division to preview; dividing it as though it
        // were an expense would answer a question nobody asked, and confidently.
        var existing = await ScopedTransaction(id)
            .OfType<Expense>()
            .Include(expense => expense.Splits)
            .ThenInclude(split => split.User)
            .Include(expense => expense.SplitRuleVersion)
            .ThenInclude(version => version!.SplitRule)
            // The bill, loaded the way the receipt service loads one, because the draft below
            // is divided by it when the rule says to. Without it the draft carries no bill at
            // all and the itemized rule refuses -- so previewing an expense that divides by
            // its receipt reported "that expense has no itemised bill on it" about an expense
            // whose bill the screen was displaying at the time.
            .Include(expense => expense.Receipt!)
            .ThenInclude(receipt => receipt.Items)
            .ThenInclude(item => item.SplitRuleVersion)
            .ThenInclude(version => (version as WeightedSplitRuleVersion)!.Participants)
            .Include(expense => expense.Receipt!)
            .ThenInclude(receipt => receipt.Items)
            .ThenInclude(item => item.SplitRuleVersion)
            .ThenInclude(version => version!.SplitRule)
            .ThenInclude(rule => rule.Group)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        // Where Update asks it, and for the same answer. Reading is broader than changing --
        // somebody who has left still sees what they paid there -- so without this the group
        // lookup below reports the group missing, and a caller previewing the edit is told
        // the group is gone (404) where the save would tell them they are (409). Same
        // question, same step, same code.
        await RefuseIfLeft(existing, ct);

        var group = request.GroupId == existing.GroupId
            ? await GroupFor(existing.GroupId, ct)
            : await GroupFor(request.GroupId, ct);

        var payer = await ParticipantOf(group, request.PaidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        group is null
                            ? "A personal expense can only have been paid by you."
                            : "The paying user is neither a member of the group nor invited to it.");

        var category = await CategoryFor(group, request.CategoryId, ct);

        // Silence about the shares is read here exactly as the PATCH reads it, and that is
        // the whole job: the model a patch is applied to carries the expense's current
        // shares, so an edit that says nothing about them carries them forward.
        //
        // Reproducing that rather than dividing afresh is what makes this a preview. An
        // edit to the amount alone is refused by the save, because shares that summed to
        // the old total do not sum to the new one, and a preview that quietly re-divided
        // would promise a success the save will not give. Refusing here means the person
        // meets it while there is still a flag to add.
        //
        // Three edits are not carried forward into. A move to another group, where the
        // shares name people who may not be in the destination. An expense with no group on
        // either side, whose single share the splitter refuses by name while the save of the
        // very same edit sails through, because GetUpdateModel hands it no shares at all;
        // group-less is excluded outright rather than left to "the group did not change",
        // which is also true of two nulls and made every preview of a personal edit fail.
        // And an explicit ask to divide it again, which is the one instruction silence
        // cannot carry -- see the parameter.
        var stated = request.Splits;

        if (!redivide && stated is null && existing.GroupId is not null && group?.Id == existing.GroupId)
        {
            stated = [.. existing.Splits.Select(split =>
                new SplitInput { UserId = split.UserId, Amount = split.Amount })];
        }

        // And then the save's own reading of that silence, which is the other half of
        // reproducing it: shares a rule produced follow the inputs they were produced from,
        // so an edit that moves one of those inputs divides again rather than carrying
        // amounts that no longer answer anything. Asked of the stored expense, which this
        // method never mutates, so it is the same question Update asks before it does.
        if (!redivide &&
            SaysNothingNew(existing.Splits, stated) &&
            InputsMoved(existing, request) &&
            await splitter.DivisionCameFromItsRule(existing, ct))
        {
            stated = null;
        }

        // The one line this method exists for, worked out the way the save works it out: the
        // division the expense was written under, replaced when the request names a rule and
        // dropped when the edit is one that hands the expense back to its category.
        var version = await DivisionFor(existing, request, group, asked: stated is null, ct);

        var draft = new Expense
        {
            Amount = request.Amount,
            Currency = group?.Currency ?? Currencies.Default,
            DateTime = request.DateTime.ToUniversalTime(),
            Name = request.Name,
            GroupId = group?.Id,
            CategoryId = category?.Id,
            UserId = payer.Id,

            SplitRuleVersionId = version,

            // The version itself and not only its id, because the answer names the rule that
            // divided it and cannot load one from an id it was handed. Only where the draft
            // kept the one the expense holds; a different one is loaded by the splitter.
            SplitRuleVersion = version == existing.SplitRuleVersionId ? existing.SplitRuleVersion : null,

            // The stored bill, which an edit never changes: a line's rule is written to the
            // receipt as it is picked, and the fields on this draft are the ones that are
            // still being typed. The itemized rule reads it off the context and refuses
            // without it.
            Receipt = existing.Receipt
        };

        // What it is divided into today, so the splitter can tell a division that changed
        // from one merely carried forward -- the difference between an edit that loses the
        // rule behind the expense and one that keeps it. Copies, because these belong to
        // the stored expense and the draft is about to have its own written over them.
        foreach (var split in existing.Splits)
            draft.Splits.Add(new TransactionSplit { UserId = split.UserId, Amount = split.Amount });

        await splitter.WriteSplitsAsync(draft, stated, ct);

        return await Describe(draft, group?.Id, ct);
    }

    /// <summary>
    /// A divided draft as the wire sees it: who owes what, under whose name, and which
    /// version of which rule decided.
    /// </summary>
    /// <remarks>
    /// The rule is read off the version the splitter settled on, and not off the category.
    /// The two disagree in the cases that matter most: shares somebody typed have no rule
    /// behind them at all and used to be reported under the category's, and an edit may be
    /// divided by a version the rule has moved on from.
    /// </remarks>
    private Task<SplitPreviewResponse> Describe(Expense draft, Guid? groupId, CancellationToken ct) =>
        Describe(groupId,
            [.. draft.Splits.Select(split => new SplitAmount(split.UserId, split.Amount))],
            draft.SplitRuleVersion,
            ct);

    /// <inheritdoc cref="Describe(Expense, Guid?, CancellationToken)"/>
    private async Task<SplitPreviewResponse> Describe(
        Guid? groupId, IReadOnlyList<SplitAmount> splits, SplitRuleVersion? version, CancellationToken ct)
    {
        var named = splits.Select(split => split.UserId).ToList();

        var names = await dbContext.Set<User>()
            .Where(user => named.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, People.Display, ct);

        var waiting = await PendingInviteesIn(groupId, ct);

        var described = splits
            .Select(split => new TransactionSplitResponse(
                split.UserId,
                names.GetValueOrDefault(split.UserId, string.Empty),
                split.Amount,
                waiting.Contains(split.UserId)))
            .ToList();

        return new SplitPreviewResponse(described, version?.SplitRule.Name, version?.SupersededAt);
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
            MerchantId = transaction.MerchantId,
            MerchantName = transaction.Merchant?.Name,
            MerchantLogoUrl = transaction.Merchant?.LogoUrl,
            Splits = splits,
            SplitRuleVersionId = transaction.SplitRuleVersionId
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

        // Asked before a single field moves, because both questions are about the expense as
        // it stands and neither can be asked afterwards: the first re-runs the division the
        // stored shares came from, and the second is a comparison against values that are
        // about to be overwritten.
        var cameFromRule = await splitter.DivisionCameFromItsRule(expense, ct);
        var inputsMoved = InputsMoved(expense, request);
        var saysNothingNew = SaysNothingNew(expense.Splits, request.Splits);

        // The destination, which is the group it is already in unless the request moves it.
        // Resolved the way a create resolves its group -- one of the caller's, or none for
        // personal -- so moving into a group the caller is not in is the same 404 as
        // creating there would be. The old shares are not carried over: they name the old
        // group's members, and the splitter below divides afresh among the new one's.
        var group = request.GroupId == expense.GroupId
            ? expense.Group
            : await GroupFor(request.GroupId, ct);

        // Which division the expense holds from here on, asked while it still holds the
        // category and the group the question is about: the one it was written under, a rule
        // the request names instead, or none where the edit hands it back to its category.
        var division = await DivisionFor(expense, request, group, asked: request.Splits is null, ct);

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

        // Both halves together, and only where the answer actually moved. EF fixes the
        // navigation up from the tracker as the expense is read, so assigning null to it
        // severs a relationship that is still there -- and a severed navigation beats the
        // key beside it, which nulled the division on every rename of an expense whose
        // version happened to be tracked.
        if (division != expense.SplitRuleVersionId)
        {
            expense.SplitRuleVersion = division is { } moved
                ? await dbContext.Set<SplitRuleVersion>().FirstAsync(version => version.Id == moved, ct)
                : null;

            expense.SplitRuleVersionId = division;
        }

        // The amount, the payer and the category can all have changed, and each of them
        // changes what everybody owed. Recomputed rather than adjusted, because there is no
        // edit for which keeping the old split would be right -- unless the caller stated
        // the division itself, which is the one case where keeping it is the whole point.
        await splitter.WriteSplitsAsync(
            expense,
            cameFromRule && inputsMoved && saysNothingNew ? null : request.Splits,
            ct);

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    /// <summary>
    /// Whether the edit changes something the division was worked out from: the amount, the
    /// payer, the category or the group.
    /// </summary>
    /// <remarks>
    /// The other three fields an edit can touch -- the name, the description, the merchant
    /// and the date -- say nothing about who owed what, so an edit confined to them must
    /// leave the shares exactly where they are. That is the shape of the 2026-09-08 incident
    /// and the reason this is asked at all.
    /// <para>
    /// Asked of the stored expense against the request, and so only answerable before the
    /// request is applied to it.
    /// </para>
    /// </remarks>
    private static bool InputsMoved(Expense expense, UpdateTransactionRequest request) =>
        expense.Amount != request.Amount ||
        expense.UserId != request.PaidByUserId ||
        expense.CategoryId != request.CategoryId ||
        expense.GroupId != request.GroupId ||
        // Naming a rule is an edit to the division itself, and the loudest of them: it is
        // somebody saying who the money was for.
        request.SplitRuleId is not null;

    /// <summary>
    /// Whether the shares the request carries are the ones the expense already holds, and so
    /// are silence about the division rather than a statement of it.
    /// </summary>
    /// <remarks>
    /// The endpoint carries the stored shares into every patch that says nothing about them
    /// -- which is what keeps a rename from re-dividing anything -- so by the time a request
    /// reaches here "keep these" and "make them exactly these" look identical, and the only
    /// thing that tells them apart is whether the amounts differ from what is stored.
    /// <para>
    /// Which matters in one place and matters a great deal there: shares somebody typed
    /// alongside a new amount are their decision about who carries it, and re-dividing over
    /// the top of them would overrule it silently. Two divisions that agree to the cent are
    /// the same division, so treating those as silence costs nothing.
    /// </para>
    /// </remarks>
    private static bool SaysNothingNew(
        ICollection<TransactionSplit> stored, IReadOnlyList<SplitInput>? stated)
    {
        if (stated is null)
            return true;

        if (stated.Count != stored.Count)
            return false;

        return stated.All(share => stored.Any(held =>
            held.UserId == share.UserId && held.Amount == share.Amount));
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
    /// Which division an edited expense should hold: the one it was written under, the
    /// version of a rule the request names instead, or none.
    /// </summary>
    /// <param name="asked">
    /// Whether the caller asked outright for the division to be worked out again -- the
    /// explicit <c>/splits</c> null that the edit dialog's <strong>Automatically</strong>
    /// and <c>--redivide</c> send. Silence is not that: the endpoint hands every patch the
    /// shares the expense already holds, so an ordinary edit arrives with them.
    /// </param>
    /// <remarks>
    /// Three answers, and the order matters.
    /// <para>
    /// A rule named in the request wins: that is somebody saying "this one is Ana's", now,
    /// about this expense.
    /// </para>
    /// <para>
    /// Otherwise the expense keeps what it holds -- which is what "divide it again by the
    /// rule it had at the time" means, and what keeps an expense recorded as one member's
    /// from being handed back to its category by an edit to its amount.
    /// </para>
    /// <para>
    /// It gives that up on a move to another group, where the version belongs to a rule the
    /// destination does not have -- and, when the division names a person outright, on an
    /// outright ask to divide it again, which is how somebody says "never mind who it was
    /// for, divide it the way this category does". That ask leaves every other division
    /// alone, because the splitter has a better answer for those: an expense asked to divide
    /// again by an ordinary rule is divided by the version it was written under, not by the
    /// rule as it reads today.
    /// </para>
    /// <para>
    /// Re-filing under another category is not one of them either. The splitter reaches for
    /// the new category's rule on its own when the version the expense holds belongs to a
    /// rule it is no longer filed under -- and keeps a division that names a person, because
    /// what a category says the money was for does not say who it was for.
    /// </para>
    /// </remarks>
    private async Task<Guid?> DivisionFor(
        Expense expense, UpdateTransactionRequest request, Group? group, bool asked, CancellationToken ct)
    {
        if (request.SplitRuleId is { } named)
            return await VersionOfRuleFor(
                group, named, SaysNothingNew(expense.Splits, request.Splits) ? null : request.Splits, ct);

        if (group?.Id != expense.GroupId)
            return null;

        if (asked && expense.SplitRuleVersionId is { } held &&
            await dbContext.Set<SoleSplitRuleVersion>().AnyAsync(version => version.Id == held, ct))
        {
            return null;
        }

        return expense.SplitRuleVersionId;
    }

    /// <summary>
    /// The division an expense names for itself: the version the named rule stands for now,
    /// checked to be one of its own group's rules.
    /// </summary>
    /// <remarks>
    /// A version and not the rule, because the version is what an expense records -- the
    /// division that produced its shares. Naming a rule is how a person says it ("all for
    /// Ana"), and this is where that becomes the one thing worth storing. Which also means a
    /// rule edited afterwards does not reach back: the expense holds the division it was
    /// written under, exactly as one divided by its category does.
    /// <para>
    /// Scoped like the category, and refused with one answer whether or not the rule exists,
    /// so guessing ids says nothing about another group's rules.
    /// </para>
    /// <para>
    /// Stated shares and a named rule are two answers to one question, and a request carrying
    /// both has not said which it means -- so it is refused rather than resolved by
    /// precedence. Silence about the shares is not carrying both: the update endpoint hands
    /// every patch the shares the expense already holds, so what tells them apart is whether
    /// those shares differ from the stored ones, which the caller works out before getting
    /// here.
    /// </para>
    /// </remarks>
    private async Task<Guid?> VersionOfRuleFor(
        Group? group, Guid? splitRuleId, IReadOnlyList<SplitInput>? stated, CancellationToken ct)
    {
        if (splitRuleId is null)
            return null;

        if (stated is not null)
            throw new ValidationException(ErrorCodes.SplitsInvalid,
                "An expense divides by a rule or by the shares you state, not both. Leave the "
                + "shares out to divide by the rule.");

        if (group is null)
            throw new ConflictException(ErrorCodes.SplitRuleNotInGroup,
                "A personal expense is not shared with anybody, so there is nothing for a rule to divide.");

        var version = await dbContext.Set<SplitRuleVersion>()
            .Where(candidate => candidate.SplitRuleId == splitRuleId &&
                                candidate.SupersededAt == null &&
                                candidate.SplitRule.Group.Id == group.Id)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);

        if (version is null)
            throw new ConflictException(ErrorCodes.SplitRuleNotInGroup,
                    "That split rule does not belong to this group.")
                .WithExtension("splitRuleId", splitRuleId);

        return version;
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

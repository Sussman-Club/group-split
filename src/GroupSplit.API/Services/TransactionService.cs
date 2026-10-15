using GroupSplit.Data;
using GroupSplit.API.Endpoints;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
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

    /// <summary>
    /// What the caller paid and what their share came to, month by month, over the
    /// expenses <paramref name="filter"/> keeps: the series behind
    /// <c>GET /transactions/monthly</c>.
    /// </summary>
    Task<List<MonthlyExposureResponse>> MonthlyExposure(TransactionFilter filter, CancellationToken ct = default);

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
    /// <para>
    /// Deleting an expense filed from a bank row undoes the filing, not the import: the row
    /// goes back to the inbox as waiting, with the receipt files it carried there. A row the
    /// bank has since withdrawn has no inbox to go back to, and goes with the expense.
    /// </para>
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
    IGroupParticipants participants,
    IReceiptAttachmentService receiptAttachments) : ITransactionService
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
        // to -- their personal group among them -- and the ones they paid for wherever
        // those are, which is what keeps an expense in a group they have since left in
        // their own listing and totals.
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

    /// <remarks>
    /// A currency to a row as well as a month, so the series can be drawn in one of
    /// them: a line that added euros to dollars would plot a number that is not an
    /// amount of anything. Its own method rather than the endpoint's body so the real
    /// database can be asked to translate it.
    /// </remarks>
    public async Task<List<MonthlyExposureResponse>> MonthlyExposure(TransactionFilter filter,
        CancellationToken ct = default)
    {
        var transactions = (await List(ct)).ApplyFilter(filter);
        var shares = await Shares(ct);
        var userId = userContext.User.Id;

        var paid = await transactions
            .Where(expense => expense.UserId == userId)
            .GroupBy(expense => new { expense.DateTime.Year, expense.DateTime.Month, expense.Currency })
            .Select(month => new
            {
                month.Key.Year,
                month.Key.Month,
                month.Key.Currency,
                Count = month.Count(),
                Total = month.Sum(expense => expense.Amount)
            })
            .ToListAsync(ct);

        var owed = await (from split in shares
                          join expense in transactions on split.TransactionId equals expense.Id
                          group split.Amount by new { expense.DateTime.Year, expense.DateTime.Month, expense.Currency }
                          into month
                          select new
                          {
                              month.Key.Year,
                              month.Key.Month,
                              month.Key.Currency,
                              Count = month.Count(),
                              Total = month.Sum()
                          })
            .ToListAsync(ct);

        var currency = TransactionApi.HeadlineCurrency(
            paid.Select(row => (row.Currency, row.Count))
                .Concat(owed.Select(row => (row.Currency, row.Count)))
                .GroupBy(row => row.Currency)
                .Select(rows => (rows.Key, rows.Sum(row => row.Count))));

        paid = [.. paid.Where(row => row.Currency == currency)];
        owed = [.. owed.Where(row => row.Currency == currency)];

        return paid.Select(row => (row.Year, row.Month))
            .Union(owed.Select(row => (row.Year, row.Month)))
            .OrderBy(month => month)
            .Select(month => new MonthlyExposureResponse(
                new DateOnly(month.Year, month.Month, 1),
                paid.FirstOrDefault(row => row.Year == month.Year && row.Month == month.Month)?.Total ?? 0m,
                owed.FirstOrDefault(row => row.Year == month.Year && row.Month == month.Month)?.Total ?? 0m)
            {
                Currency = currency
            })
            .ToList();
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
        var groupId = transaction.GroupId;

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
        var currency = CurrencyFor(group, request.Currency);

        var payer = await ParticipantOf(group, paidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        PayerNotInGroup(group));

        var category = await CategoryFor(group, request.CategoryId, ct);
        var selectedRuleVersionId = await VersionOfRuleFor(group, request.SplitRuleId,
            request.Splits, ct);

        var expense = new Expense
        {
            Amount = request.Amount,
            Currency = currency,
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
            SplitRuleVersionId = selectedRuleVersionId,
            User = payer
        };

        if (selectedRuleVersionId is { } selected)
            await splitter.WriteSplitsByRuleAsync(expense, selected, ct);
        else
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
        var currency = CurrencyFor(group, request.Currency);

        var payer = await ParticipantOf(group, paidByUserId, ct)
                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                        PayerNotInGroup(group));

        var category = await CategoryFor(group, request.CategoryId, ct);
        var selectedRuleVersionId = await VersionOfRuleFor(group, request.SplitRuleId,
            request.Splits, ct);

        // Ids only, and never added to the context: the splitter reads the ids when the
        // navigations are absent, so nothing here is reachable from a tracked entity and
        // there is no save on this path to reach it with.
        var draft = new Expense
        {
            Amount = request.Amount,
            Currency = currency,
            DateTime = request.DateTime.ToUniversalTime(),
            Name = request.Name,
            GroupId = group.Id,
            CategoryId = category?.Id,
            SplitRuleVersionId = selectedRuleVersionId,
            UserId = payer.Id
        };

        if (selectedRuleVersionId is { } selected)
            await splitter.WriteSplitsByRuleAsync(draft, selected, ct);
        else
            await splitter.WriteSplitsAsync(draft, request.Splits, ct);

        return await Describe(draft, group.Id, ct);
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

        // The same snapshot Update takes, so the preview refuses what the save would.
        var departed = await participants.DepartedOn(existing, ct);

        var group = request.GroupId == existing.GroupId
            ? await GroupFor(existing.GroupId, ct)
            : await GroupFor(request.GroupId, ct);

        var groupMoved = group.Id != existing.GroupId;

        // Before anything is divided, as the save refuses it: a preview that showed the
        // shares of a move the save will not make is a promise nobody can keep.
        RefuseCurrencyMove(existing, group);

        // And the payer asked about only when it changes, as the save asks it.
        var payerId = request.PaidByUserId == existing.UserId && !groupMoved
            ? existing.UserId
            : (await ParticipantOf(group, request.PaidByUserId, ct)
               ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                   PayerNotInGroup(group))).Id;

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
        // shares name people who may not be in the destination. A personal expense, whose
        // single share the splitter refuses by name while the save of the very same edit
        // sails through, because GetUpdateModel hands it no shares at all; it is excluded
        // outright rather than left to "the group did not change", which is also true of a
        // personal expense staying personal and made every preview of a personal edit fail.
        // And an explicit ask to divide it again, which is the one instruction silence
        // cannot carry -- see the parameter.
        var stated = request.Splits;

        if (!redivide && stated is null && !existing.IsPersonal && group.Id == existing.GroupId)
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
            // Its own, wherever the edit puts it: a move into a group in another currency
            // was refused above, and a move out of one keeps what the money was spent in.
            Currency = existing.Currency,
            DateTime = request.DateTime.ToUniversalTime(),
            Name = request.Name,
            GroupId = group.Id,
            CategoryId = category?.Id,
            UserId = payerId,

            SplitRuleVersionId = version,

            // The version itself and not only its id, because the answer names the rule that
            // divided it and cannot load one from an id it was handed. Only where the draft
            // kept the one the expense holds; a different one is loaded by the splitter.
            SplitRuleVersion = version == existing.SplitRuleVersionId ? existing.SplitRuleVersion : null,

            // The stored bill, which an edit never changes: a line's rule is written to the
            // receipt as it is picked, and the fields on this draft are the ones that are
            // still being typed. The itemized rule reads it off the context and refuses
            // without it. A copy with no line rules on a move, which is what the save leaves.
            Receipt = groupMoved && existing.Receipt is { } moving
                ? BillRetotal.Copy(moving, withoutRules: true)
                : existing.Receipt
        };

        // And re-totalled to the new amount where the save would re-total it -- see Update --
        // on a copy, so the bill the context is tracking is never the one that moves.
        if (request.Amount != existing.Amount
            && (request.SplitRuleId is not null || stated is null)
            && draft.Receipt is { } bill
            && bill.Total == existing.Amount
            && await DividesByItsBill(draft, request.SplitRuleId is not null ? version : null, ct))
        {
            draft.Receipt = ReferenceEquals(bill, existing.Receipt)
                ? BillRetotal.Copy(bill, withoutRules: false)
                : bill;
            BillRetotal.To(draft.Receipt, request.Amount);
        }

        // What it is divided into today, so the splitter can tell a division that changed
        // from one merely carried forward -- the difference between an edit that loses the
        // rule behind the expense and one that keeps it. Copies, because these belong to
        // the stored expense and the draft is about to have its own written over them.
        //
        // Not on a move, where they name the old group's people, as the save drops them.
        if (!groupMoved)
        {
            foreach (var split in existing.Splits)
                draft.Splits.Add(new TransactionSplit { UserId = split.UserId, Amount = split.Amount });
        }

        if (request.SplitRuleId is not null)
            await splitter.WriteSplitsByRuleAsync(draft, version!.Value, ct);
        else
            await splitter.WriteSplitsAsync(draft, stated, ct);

        DepartedPositions.RefuseIfMoved(departed, draft, existing.GroupId);

        return await Describe(draft, group.Id, ct);
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
    private Task<SplitPreviewResponse> Describe(Expense draft, Guid groupId, CancellationToken ct) =>
        Describe(groupId,
            [.. draft.Splits.Select(split => new SplitAmount(split.UserId, split.Amount))],
            draft.SplitRuleVersion,
            ct);

    /// <inheritdoc cref="Describe(Expense, Guid, CancellationToken)"/>
    private async Task<SplitPreviewResponse> Describe(
        Guid groupId, IReadOnlyList<SplitAmount> splits, SplitRuleVersion? version, CancellationToken ct)
    {
        var named = splits.Select(split => split.UserId).ToList();

        var names = await dbContext.Set<User>()
            .Where(user => named.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, People.Display, ct);

        var guests = await GuestsIn(groupId, ct);

        var described = splits
            .Select(split => new TransactionSplitResponse(
                split.UserId,
                names.GetValueOrDefault(split.UserId, string.Empty),
                split.Amount,
                guests.Contains(split.UserId)))
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

        // Who among the people on this transaction is one of the group's guests. One query
        // for the whole transaction rather than one per share, and empty for a personal
        // expense, whose group has nobody to invite.
        var guests = await GuestsIn(transaction.GroupId, ct);

        // Read, not re-derived. The amounts below are the ones the group's balances are
        // summed from, so a detail view that computed its own could disagree with them.
        var splits = transaction.Splits
            .Select(split => new TransactionSplitResponse(
                split.User.Id,
                People.Display(split.User),
                split.Amount,
                guests.Contains(split.User.Id)))
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
            Currency = transaction.Currency,
            DateTime = transaction.DateTime,
            GroupId = transaction.GroupId,
            GroupName = transaction.Group.Name,
            PaidByUserId = transaction.User.Id,
            PaidByUserName = People.Display(transaction.User),
            PaidByIsGuest = guests.Contains(transaction.User.Id),
            PaidToUserId = recipientSplit?.UserId,
            PaidToUserName = recipientSplit?.UserName,
            CategoryId = expense?.CategoryId,
            Category = expense?.Category?.Name,
            MerchantId = transaction.MerchantId,
            MerchantName = transaction.Merchant?.Name,
            MerchantLogoUrl = transaction.Merchant?.LogoUrl,
            Splits = splits,
            SplitRuleVersionId = transaction.SplitRuleVersionId,
            CoversThrough = transfer?.CoversThrough
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
            CoversThrough = (transaction as Transfer)?.CoversThrough,
            Splits = transaction.IsPersonal
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

        // What everybody who has left holds on it, taken before a single field moves. They
        // left square, and the group's balances add up only while that stays true, so no
        // edit may change it; the check is made against this once the edit is applied.
        var departed = await participants.DepartedOn(transaction, ct);
        var recordedIn = transaction.GroupId;

        if (transaction is Transfer transfer)
        {
            // Participants on both ends, as for an expense: the members record what a guest
            // pays and is paid.
            //
            // Asked only of an end that is changing. The settlement that let somebody leave
            // names them, and a corrected date or note on it must not be refused because
            // they have gone.
            var payerId = request.PaidByUserId;

            // The checks creating one makes, made again here because the edit model is the
            // one expenses use and cannot carry them: a refund is a negative expense, while
            // a negative repayment is a debt filed as paying one back.
            if (request.Amount <= 0)
                throw new ValidationException(ErrorCodes.SettlementAmountNotPositive,
                    "A settlement's amount must be greater than 0.");

            if (request.Splits is { Count: > 1 })
                throw new ValidationException(ErrorCodes.SplitsInvalid,
                    "A settlement is paid to exactly one person.");

            // Whoever ends up on the receiving end, asked whichever way the patch got there:
            // the share it states, or -- when it removed or emptied the shares -- the one the
            // settlement already has. Asking only of a stated share let a patch that dropped
            // the shares and moved the payer onto the recipient write a payment to oneself.
            var recipientId = (request.Splits is [var stated] ? stated.UserId : transfer.Splits.FirstOrDefault()?.UserId)
                              ?? throw new ValidationException(ErrorCodes.SplitsInvalid,
                                  "This settlement records nobody it was paid to. Name them in /splits.");

            if (recipientId == payerId)
                throw new ConflictException(ErrorCodes.SettlementWithSelf,
                    "A settlement needs two different people.");

            if (payerId != transfer.UserId)
            {
                transfer.User = await ParticipantOf(transfer.Group, payerId, ct)
                                ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                                    "The paying user is not a member or guest of the group.");
            }

            transfer.Amount = request.Amount;
            transfer.DateTime = request.DateTime.ToUniversalTime();
            transfer.Description = request.Description;
            transfer.CoversThrough = request.CoversThrough;

            if (request.Splits is { Count: > 0 })
            {
                var existingSplit = transfer.Splits.FirstOrDefault();

                if (existingSplit?.UserId != recipientId)
                {
                    var recipient = await ParticipantOf(transfer.Group, recipientId, ct)
                                    ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                                        "The recipient is not a member or guest of the group.");

                    if (existingSplit is not null)
                    {
                        existingSplit.User = recipient;
                        existingSplit.UserId = recipient.Id;
                    }
                    else
                    {
                        existingSplit = new TransactionSplit { User = recipient, Amount = request.Amount };
                        transfer.Splits.Add(existingSplit);
                    }
                }

                existingSplit!.Amount = request.Amount;
            }
            else
            {
                foreach (var split in transfer.Splits)
                {
                    split.Amount = request.Amount;
                }
            }

            DepartedPositions.RefuseIfMoved(departed, transfer, recordedIn);

            await dbContext.SaveChangesAsync(ct);
            return transfer;
        }

        var expense = (Expense)transaction;

        if (request.CoversThrough is not null)
            throw new ValidationException(ErrorCodes.NotASettlement,
                "Only a settlement settles a period. An expense has no day it covers through.");

        // Asked before a single field moves, because both questions are about the expense as
        // it stands and neither can be asked afterwards: the first re-runs the division the
        // stored shares came from, and the second is a comparison against values that are
        // about to be overwritten.
        // Null is the caller's personal group, said the short way -- as on create, and as
        // the edit model of a personal expense still carries it to older clients. Named
        // outright before anything compares groups, so a personal expense staying personal
        // is not read as a move.
        request.GroupId ??= userContext.User.Id;

        var cameFromRule = await splitter.DivisionCameFromItsRule(expense, ct);
        var inputsMoved = InputsMoved(expense, request);
        var saysNothingNew = SaysNothingNew(expense.Splits, request.Splits);
        var amountMoved = expense.Amount != request.Amount;
        var previousAmount = expense.Amount;
        var groupMoved = expense.GroupId != request.GroupId;
        var payerMoved = expense.UserId != request.PaidByUserId;

        // What a departed payer paid *is* the amount, so no new amount can leave their part
        // as it was -- whatever the shares say. Refused here, by name, before the shares are
        // checked: shares carried over from before the edit no longer add up to the new
        // amount, and "the shares have to add up" was the answer the person got, which sent
        // them looking at the wrong thing.
        if (amountMoved &&
            departed.FirstOrDefault(position => position.UserId == expense.UserId && position.Paid != 0m)
                is { } departedPayer)
            throw DepartedPositions.Refusal([departedPayer.UserId]);

        // The destination, which is the group it is already in unless the request moves it.
        // Resolved the way a create resolves its group -- one of the caller's, their
        // personal one among them -- so moving into a group the caller is not in is the same 404 as
        // creating there would be. The old shares are not carried over: they name the old
        // group's members, and the splitter below divides afresh among the new one's.
        var group = request.GroupId == expense.GroupId
            ? expense.Group
            : await GroupFor(request.GroupId, ct);

        // Refused before a single field moves. The expense keeps the currency it was
        // recorded in, so a group in another one cannot take it: its balances are one
        // currency, and adding these figures to them would relabel the money rather than
        // convert it.
        RefuseCurrencyMove(expense, group);

        // Which division the expense holds from here on, asked while it still holds the
        // category and the group the question is about: the one it was written under, a rule
        // the request names instead, or none where the edit hands it back to its category.
        var division = await DivisionFor(expense, request, group, asked: request.Splits is null, ct);

        if (groupMoved)
        {
            expense.Group = group;
            expense.GroupId = group.Id;
            // The currency stays. Into a group it already matches -- anything else was
            // refused above -- and into a personal one, which takes any currency, it is
            // still what the money was spent in.

            if (IsPersonal(group))
            {
                request.Splits = null;
            }

            // The old group's shares go before anything divides again. They name the old
            // group's people, and left in place they would read as this expense's holders
            // in a group they were never in.
            dbContext.RemoveRange(expense.Splits);
            expense.Splits.Clear();

            // And so do the rules on its bill's lines, for the same reason one level down:
            // they are the old group's rules, and the new group can read the bill.
            if (await BillOf(expense, ct) is { } moved)
                BillRetotal.ClearRules(moved);
        }

        // Asked of the payer only when it is changing. The person who paid for a dinner a
        // year ago may have left since, and that is no reason to refuse correcting its name.
        if (payerMoved || groupMoved)
        {
            expense.User = await ParticipantOf(group, request.PaidByUserId, ct)
                           ?? throw new ConflictException(ErrorCodes.TransactionPayerNotInGroup,
                               PayerNotInGroup(group));
        }

        var category = await CategoryFor(group, request.CategoryId, ct);

        expense.Amount = request.Amount;
        expense.DateTime = request.DateTime.ToUniversalTime();
        expense.Name = request.Name;
        expense.Description = request.Description;
        expense.Category = category;
        expense.CategoryId = category?.Id;
        expense.MerchantId = await MerchantFor(request.MerchantId, ct);

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

        // An expense divided by its bill is divided again by that bill below, and the bill
        // has to come to the new amount for that to work -- so it is re-totalled first,
        // the difference on its tip. Only on the branches that divide by a rule: shares
        // somebody stated are their own answer, and leave the bill as it was.
        if (amountMoved
            && (request.SplitRuleId is not null || (cameFromRule && saysNothingNew) || request.Splits is null)
            && await DividesByItsBill(expense, request.SplitRuleId is not null ? division : null, ct)
            && await BillOf(expense, ct) is { } bill
            && bill.Total == previousAmount)
            BillRetotal.To(bill, expense.Amount);

        // The amount, the payer and the category can all have changed, and each of them
        // changes what everybody owed. Recomputed rather than adjusted, because there is no
        // edit for which keeping the old split would be right -- unless the caller stated
        // the division itself, which is the one case where keeping it is the whole point.
        if (request.SplitRuleId is not null)
            await splitter.WriteSplitsByRuleAsync(expense, division!.Value, ct);
        else if (cameFromRule && inputsMoved && saysNothingNew)
            await splitter.WriteSplitsAsync(expense, null, ct);
        else if (request.Splits is not null && saysNothingNew && !amountMoved && !groupMoved)
        {
            // The shares the expense already holds, carried in by the endpoint, on an edit
            // that moved nothing they were worked out from: a rename, a note, a date, a
            // payer or a category whose rule did not produce them. Written back as they are
            // they would be checked against today's participants, and an expense somebody
            // has since left could not be renamed at all. There is nothing to write.
        }
        else
            await splitter.WriteSplitsAsync(expense, request.Splits, ct);

        // Last, against the positions taken before anything moved: whatever the edit was,
        // what a departed participant paid and owed here is what it was.
        DepartedPositions.RefuseIfMoved(departed, expense, recordedIn);

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    /// <summary>
    /// The expense's bill, with its lines and the rules on them, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Put on the expense as well as returned, so the splitter divides by this tracked copy
    /// -- the one an edit has just changed -- rather than reading the bill again.
    /// </remarks>
    private async Task<Receipt?> BillOf(Expense expense, CancellationToken ct) =>
        expense.Receipt ??= await dbContext.Set<Receipt>()
            .Include(receipt => receipt.Items)
            .ThenInclude(item => item.SplitRuleVersion)
            .ThenInclude(version => (version as WeightedSplitRuleVersion)!.Participants)
            .Include(receipt => receipt.Items)
            .ThenInclude(item => item.SplitRuleVersion)
            .ThenInclude(version => version!.SplitRule)
            .ThenInclude(rule => rule.Group)
            .FirstOrDefaultAsync(receipt => receipt.ExpenseId == expense.Id, ct);

    /// <summary>
    /// Whether the division about to be written is the expense's bill: the version a request
    /// names, when it names one, and otherwise whatever the splitter would divide it by.
    /// </summary>
    private async Task<bool> DividesByItsBill(Expense expense, Guid? named, CancellationToken ct) =>
        named is { } version
            ? await dbContext.Set<SplitRuleVersion>().OfType<ItemizedSplitRuleVersion>()
                .AnyAsync(candidate => candidate.Id == version, ct)
            : await splitter.DividesByItsBill(expense, ct);

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
        var transaction = await ScopedTransaction(id)
            .Include(candidate => candidate.Splits)
            .FirstOrDefaultAsync(ct);

        if (transaction is null)
            throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        await RefuseIfLeft(transaction, ct);

        // Deleting it takes everybody's part in it out of the group's balances, and the part
        // of somebody who has left goes with nobody on the page to show for it. They left
        // square; this would leave them owed, or owing, with no way to settle. The same holds
        // for the settlement that let them leave.
        var moved = (await participants.DepartedOn(transaction, ct))
            .Where(position => !position.IsNothing)
            .Select(position => position.UserId)
            .ToList();

        if (moved.Count > 0)
            throw DepartedPositions.Refusal(moved);

        var row = transaction is Expense ? await FiledFrom(transaction, ct) : null;

        // Returning only while the bank still stands behind the row. One it has withdrawn
        // would come back as a payment that no longer exists.
        var returning = row is { RemovedAt: null } ? row : null;

        if (transaction is Expense)
            await receiptAttachments.DeleteForExpense(id, returning?.Id, ct);

        // Saved with the delete rather than before it, so a delete that fails leaves the row
        // filed against the expense that is still there.
        if (returning is not null)
            returning.Status = BankTransactionStatus.New;
        else if (row is not null)
            dbContext.Remove(row);

        dbContext.Remove(transaction);

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The bank row this expense was filed from, while it is still the row's filing.
    /// </summary>
    /// <remarks>
    /// The foreign key is SetNull, which used to be the whole of what deleting an expense
    /// did to its row: the expense went, and the row stayed <c>Filed</c> with nothing filed
    /// from it. Filing and linking then refused it as already an expense, and ignoring it
    /// said to delete the expense -- the step that had just trapped it.
    /// <para>
    /// Only a row whose status is still <see cref="BankTransactionStatus.Filed"/>. One a
    /// posted row has superseded keeps that status, because the posted row holds the story
    /// now; the expense still pointing at it is the one a supersede could not move.
    /// </para>
    /// </remarks>
    private async Task<BankTransaction?> FiledFrom(Transaction transaction, CancellationToken ct)
    {
        if (transaction.BankTransactionId is not { } rowId)
            return null;

        return await dbContext.Set<BankTransaction>()
            .FirstOrDefaultAsync(row => row.Id == rowId && row.Status == BankTransactionStatus.Filed, ct);
    }

    /// <summary>
    /// The group the expense belongs to: one of the caller's, or their personal group when
    /// the request names none.
    /// </summary>
    /// <remarks>
    /// Null stays a way of saying "mine" because it is how every client has said it since
    /// personal expenses had no group -- the CLI's <c>--personal</c>, the inbox's "Keep
    /// personal" -- and the personal group's id is not one a client has to look up.
    /// </remarks>
    private async Task<Group> GroupFor(Guid? groupId, CancellationToken ct)
    {
        var currentUser = userContext.User;
        var id = groupId ?? currentUser.Id;

        return await dbContext.Entry(currentUser).Collection(u => u.Groups).Query()
                   .FirstOrDefaultAsync(@group => @group.Id == id, ct)
               ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
    }

    /// <summary>Whether <paramref name="group"/> is the caller's personal group.</summary>
    private bool IsPersonal(Group group) => group.IsPersonalOf(userContext.User.Id);

    private string PayerNotInGroup(Group group) =>
        IsPersonal(group)
            ? "A personal expense can only have been paid by you."
            : "The paying user is neither a member of the group nor invited to it.";

    /// <summary>
    /// The currency a new expense is recorded in: the one it was stated in, or the group's
    /// when nothing was said.
    /// </summary>
    /// <remarks>
    /// A personal expense may be in any currency, because nothing sums it with anybody
    /// else's. It used to be stamped with the default whatever it was given, so a euro row
    /// kept personal became the same number of dollars -- a different amount of money,
    /// written down as though it were the one the bank reported. A group's balances are in
    /// one currency and conversion is out of scope, so there a stated currency has to be
    /// the group's own.
    /// </remarks>
    private string CurrencyFor(Group group, string? stated)
    {
        var currency = string.IsNullOrWhiteSpace(stated) ? null : stated.Trim().ToUpperInvariant();

        if (IsPersonal(group))
            return currency ?? group.Currency;

        if (currency is not null && !SameCurrency(currency, group.Currency))
            throw CurrencyMismatch(currency, group);

        return group.Currency;
    }

    /// <summary>
    /// Refuses to put an expense into a group that keeps its balances in another currency.
    /// </summary>
    /// <remarks>
    /// The same refusal filing into the group meets, on the other way in. A move used to
    /// take the destination's currency, which relabelled the amount rather than converting
    /// it -- twenty euros became twenty dollars in everybody's balance. Never into the
    /// caller's personal group, which takes any currency for the reason above.
    /// </remarks>
    private void RefuseCurrencyMove(Expense expense, Group destination)
    {
        if (!IsPersonal(destination) && destination.Id != expense.GroupId &&
            !SameCurrency(expense.Currency, destination.Currency))
            throw CurrencyMismatch(expense.Currency, destination);
    }

    private static bool SameCurrency(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static DomainException CurrencyMismatch(string currency, Group group) =>
        new ConflictException(ErrorCodes.CurrencyMismatch,
                $"This is in {currency} and the group keeps its balances in {group.Currency}.")
            .WithExtension("transactionCurrency", currency)
            .WithExtension("groupCurrency", group.Currency);

    /// <summary>
    /// Which of a group's participants are its guests, as ids. Empty for a personal group,
    /// which can have none.
    /// </summary>
    private async Task<HashSet<Guid>> GuestsIn(Guid groupId, CancellationToken ct)
    {
        return [.. await dbContext.Set<GroupGuest>()
            .Where(guest => guest.GroupId == groupId)
            .Select(guest => guest.ParticipantUserId)
            .ToListAsync(ct)];
    }

    /// <summary>
    /// The participant of <paramref name="group"/> with that id: a member, or one of its
    /// guests. In a personal group that is the caller alone, since it has no other member.
    /// </summary>
    private Task<User?> ParticipantOf(Group group, Guid userId, CancellationToken ct) =>
        participants.Find(group.Id, userId, ct);

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
        Expense expense, UpdateTransactionRequest request, Group group, bool asked, CancellationToken ct)
    {
        if (request.SplitRuleId is { } named)
            return await VersionOfRuleFor(
                group, named, SaysNothingNew(expense.Splits, request.Splits) ? null : request.Splits, ct);

        if (group.Id != expense.GroupId)
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
        Group group, Guid? splitRuleId, IReadOnlyList<SplitInput>? stated, CancellationToken ct)
    {
        if (splitRuleId is null)
            return null;

        if (stated is not null)
            throw new ValidationException(ErrorCodes.SplitsInvalid,
                "An expense divides by a rule or by the shares you state, not both. Leave the "
                + "shares out to divide by the rule.");

        if (IsPersonal(group))
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

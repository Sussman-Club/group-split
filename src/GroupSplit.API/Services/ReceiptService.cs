using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IReceiptService
{
    Task<Receipt> ForExpense(Guid expenseId, CancellationToken ct = default);
    Task<Receipt> SaveForExpense(Guid expenseId, SaveReceiptRequest request, CancellationToken ct = default);
    Task<Receipt> SetRule(Guid expenseId, Guid itemId, SetReceiptItemRuleRequest request, CancellationToken ct = default);
    Task<ReceiptDivisionResponse> Preview(Guid expenseId, CancellationToken ct = default);
    Task<Expense> Divide(Guid expenseId, CancellationToken ct = default);
    Task<ReceiptResponse> ResponseFor(Receipt receipt, Guid? expenseId = null, CancellationToken ct = default);
    Task DeleteForExpense(Guid expenseId, CancellationToken ct = default);
}

public class ReceiptService(AppDbContext dbContext, ICurrentUser userContext,
    IGroupParticipants participants, IExpenseSplitter splitter, ISplitRuleHandler handlers) : IReceiptService
{
    public async Task<Receipt> ForExpense(Guid expenseId, CancellationToken ct = default)
    {
        await VisibleExpense(expenseId, ct);
        return await Loaded().FirstOrDefaultAsync(r => r.ExpenseId == expenseId, ct)
            ?? throw new NotFoundException(ErrorCodes.ReceiptNotFound, "This expense has no itemized bill.");
    }

    /// <summary>
    /// The bill on the wire, with the flag that says whether dividing by it would work.
    /// </summary>
    /// <remarks>
    /// Answered by actually dividing it and throwing the answer away, rather than by a second
    /// set of checks that would eventually disagree with the first: a screen offering a
    /// button that then refuses is worse than one that never offered it. The three refusals
    /// caught here are the three the division raises about the bill -- a line with no rule, a
    /// rule that no longer fits, figures that do not add up -- and each of them means "not
    /// yet", not "something went wrong".
    /// </remarks>
    public async Task<ReceiptResponse> ResponseFor(Receipt receipt, Guid? expenseId = null, CancellationToken ct = default)
    {
        var expense = await VisibleExpense(receipt.ExpenseId, ct);
        // Personal receipts are useful as itemized records even though they never produce
        // a group division. They remain editable by their owner, while division stays a
        // shared-expense concern.
        var canEdit = await StillInTheGroup(expense, ct);
        var canDivide = expense.GroupId is not null && canEdit;
        if (canDivide)
        {
            try { await Calculate(expense, receipt, ct); }
            catch (ValidationException) { canDivide = false; }
            catch (UnprocessableException) { canDivide = false; }
            catch (ConflictException) { canDivide = false; }
        }
        return receipt.ToResponse(handlers, canDivide, await splitter.DividesByItsBill(expense, ct))
            with { CanEdit = canEdit };
    }

    public async Task<Receipt> SaveForExpense(Guid expenseId, SaveReceiptRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expense = await MineToChange(expenseId, ct);
        var existing = await Loaded().FirstOrDefaultAsync(r => r.ExpenseId == expenseId, ct);
        var members = expense.GroupId is { } groupId
            ? await participants.IdsOf(groupId, ct)
            : [expense.Payer];
        var versions = await Versions().Where(v => request.Items.Select(i => i.SplitRuleVersionId).Contains(v.Id)).ToListAsync(ct);
        // An id on an incoming line says "this is that stored line, corrected", so it has to
        // name a line of this bill and name it once. One from another bill would move a line
        // between receipts, and the same id twice would leave the update writing two lines
        // over one.
        var ids = request.Items.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToList();
        if (ids.Count != ids.Distinct().Count() || ids.Any(id => existing?.Items.All(i => i.Id != id) != false))
            throw new ValidationException(ErrorCodes.ReceiptInvalid, "An item id is repeated or does not belong to this bill.");

        // Validate a detached draft first: a refused request never mutates the tracked bill.
        var draft = new Receipt { ExpenseId = expenseId, Subtotal = request.Subtotal,
            Tax = request.Tax, Tip = request.Tip, Total = request.Total };
        for (var position = 0; position < request.Items.Count; position++)
        {
            var input = request.Items[position];
            var version = input.SplitRuleVersionId is { } versionId
                ? versions.FirstOrDefault(v => v.Id == versionId)
                    ?? throw new ValidationException(ErrorCodes.ReceiptInvalid, "The item's split rule version was not found.")
                : null;
            if (version is not null) ReceiptSplitCalculator.ValidateRule(version, expense.GroupId, members, handlers);
            draft.Items.Add(new ReceiptItem { Id = input.Id ?? Guid.NewGuid(), Position = position,
                Name = input.Name?.Trim() ?? "",
                NormalizedName = input.NormalizedName?.Trim().ToLowerInvariant()
                    ?? input.Name?.Trim().ToLowerInvariant() ?? "",
                Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
                UnitPrice = input.UnitPrice, Quantity = input.Quantity, TotalPrice = input.TotalPrice,
                TaxAmount = input.TaxAmount, SplitRuleVersionId = version?.Id, SplitRuleVersion = version });
        }
        ReceiptSplitCalculator.RefuseIfFiguresDisagree(draft);
        if (draft.Total != expense.Amount)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp, "The bill total must equal the expense amount.");

        if (existing is null)
        {
            draft.Expense = expense;
            dbContext.Add(draft);
        }
        else
        {
            // Updated in place rather than replaced, so a line keeps its id: the ids are how
            // the next correction names these lines, and dropping and re-adding the bill
            // would silently clear every line's rule on the way through.
            existing.Subtotal = draft.Subtotal; existing.Tax = draft.Tax;
            existing.Tip = draft.Tip; existing.Total = draft.Total;
            foreach (var removed in existing.Items.Where(i => draft.Items.All(d => d.Id != i.Id)).ToList())
            { dbContext.Remove(removed); existing.Items.Remove(removed); }
            foreach (var line in draft.Items)
            {
                var stored = existing.Items.FirstOrDefault(i => i.Id == line.Id);
                if (stored is null)
                { line.ReceiptId = existing.Id; existing.Items.Add(line); dbContext.Entry(line).State = EntityState.Added; }
                else
                {
                    stored.Position = line.Position; stored.Name = line.Name; stored.NormalizedName = line.NormalizedName;
                    stored.Description = line.Description;
                    stored.UnitPrice = line.UnitPrice; stored.Quantity = line.Quantity; stored.TotalPrice = line.TotalPrice;
                    stored.TaxAmount = line.TaxAmount; stored.SplitRuleVersionId = line.SplitRuleVersionId;
                    stored.SplitRuleVersion = line.SplitRuleVersion;
                }
            }
        }
        var receiptId = existing?.Id ?? draft.Id;
        var unlinkedAttachments = await dbContext.Set<ReceiptAttachment>()
            .Where(attachment => attachment.ExpenseId == expenseId && attachment.ReceiptId == null)
            .ToListAsync(ct);
        foreach (var attachment in unlinkedAttachments)
        {
            attachment.ReceiptId = receiptId;
        }

        await dbContext.SaveChangesAsync(ct);
        return await ForExpense(expenseId, ct);
    }

    public async Task<Receipt> SetRule(Guid expenseId, Guid itemId, SetReceiptItemRuleRequest request, CancellationToken ct = default)
    {
        var expense = await MineToChange(expenseId, ct);
        var receipt = await ForExpense(expenseId, ct);
        var item = receipt.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new NotFoundException(ErrorCodes.ReceiptItemNotFound, "Item not found on this bill.");
        SplitRuleVersion? version = null;
        if (request.SplitRuleVersionId is { } id)
        {
            version = await Versions().FirstOrDefaultAsync(v => v.Id == id, ct)
                ?? throw new ValidationException(ErrorCodes.ReceiptInvalid, "Split rule version not found.");
            ReceiptSplitCalculator.ValidateRule(version, expense.GroupId, await Members(expense, ct), handlers);
        }
        item.SplitRuleVersionId = version?.Id;
        item.SplitRuleVersion = version;
        await dbContext.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task<ReceiptDivisionResponse> Preview(Guid expenseId, CancellationToken ct = default)
    {
        var expense = await VisibleExpense(expenseId, ct);
        return await Calculate(expense, await ForExpense(expenseId, ct), ct);
    }

    private async Task<ReceiptDivisionResponse> Calculate(Expense expense, Receipt receipt, CancellationToken ct)
    {
        if (expense.Amount != receipt.Total)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp, "The bill total must equal the expense amount.");
        var members = await Members(expense, ct);
        var shares = ReceiptSplitCalculator.Divide(receipt, expense.Payer, expense.GroupId, members, handlers);
        var subtotals = ReceiptSplitCalculator.Divide(receipt, expense.Payer, expense.GroupId, members, handlers, true)
            .ToDictionary(s => s.UserId, s => s.Amount);
        return new ReceiptDivisionResponse(receipt.Id, receipt.Total,
            shares.Select(s => new ReceiptShareResponse(s.UserId, subtotals.GetValueOrDefault(s.UserId), s.Amount)).ToList());
    }

    /// <summary>
    /// Stores the division the bill comes to.
    /// </summary>
    /// <remarks>
    /// Two ways to store the same figures, because the expense's own rule decides what they
    /// are. Under an itemized rule the bill *is* the division, so the splitter is left to
    /// divide by it and the expense goes on saying which version it was divided by. Under any
    /// other rule the shares are the bill's answer to a question the rule answers differently,
    /// so they are written as stated amounts -- the same as typing them -- and an ordinary
    /// edit will not quietly re-divide them by the rule.
    /// </remarks>
    public async Task<Expense> Divide(Guid expenseId, CancellationToken ct = default)
    {
        var expense = await MineToChange(expenseId, ct);
        var receipt = await ForExpense(expenseId, ct);
        var preview = await Calculate(expense, receipt, ct);
        expense.Receipt = receipt;
        if (await splitter.DividesByItsBill(expense, ct))
            await splitter.WriteSplitsAsync(expense, ct);
        else
            await splitter.WriteSplitsAsync(expense,
                preview.Shares.Select(s => new SplitInput { UserId = s.UserId, Amount = s.Amount }).ToList(), ct);
        await dbContext.SaveChangesAsync(ct);
        return expense;
    }

    public async Task DeleteForExpense(Guid expenseId, CancellationToken ct = default)
    {
        await MineToChange(expenseId, ct);
        dbContext.Remove(await ForExpense(expenseId, ct));
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyCollection<Guid>> Members(Expense expense, CancellationToken ct) =>
        expense.GroupId is { } groupId ? await participants.IdsOf(groupId, ct) : [expense.Payer];

    private IQueryable<SplitRuleVersion> Versions() => dbContext.Set<SplitRuleVersion>()
        .Include(v => (v as WeightedSplitRuleVersion)!.Participants).Include(v => v.SplitRule).ThenInclude(r => r.Group);

    private IQueryable<Receipt> Loaded() => dbContext.Set<Receipt>()
        .Include(r => r.Items).ThenInclude(i => i.SplitRuleVersion).ThenInclude(v => (v as WeightedSplitRuleVersion)!.Participants)
        .Include(r => r.Items).ThenInclude(i => i.SplitRuleVersion).ThenInclude(v => v!.SplitRule).ThenInclude(r => r.Group);

    private async Task<Expense> VisibleExpense(Guid expenseId, CancellationToken ct)
    {
        var currentUser = userContext.User;

        var groups = dbContext.Entry(currentUser).Collection(user => user.Groups).Query();

        var expense = await dbContext.Set<Expense>()
            .Include(candidate => candidate.Splits)
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == expenseId &&
                (groups.Any(@group => @group.Id == candidate.GroupId) ||
                 candidate.UserId == currentUser.Id), ct);

        // A transfer reached by id takes this path too, because Set<Expense>() filters it
        // out -- and "there is no expense with that id" is what a repayment is, as far as a
        // bill is concerned.
        return expense ?? throw new NotFoundException(ErrorCodes.TransactionNotFound,
            "Expense not found.");
    }

    /// <summary>
    /// The expense, if the caller may <em>change</em> it -- which is narrower than being able
    /// to read it.
    /// </summary>
    /// <remarks>
    /// Somebody who has left a group keeps seeing the expenses they paid for there, and must
    /// not go on moving that group's balances: attaching a bill, re-claiming its lines and
    /// dividing by it rewrites what everybody still in the group owes. The same refusal
    /// <c>TransactionService</c> raises on an edit, in the same code, because a receipt is
    /// only ever another way of deciding a division.
    /// </remarks>
    private async Task<Expense> MineToChange(Guid expenseId, CancellationToken ct)
    {
        var expense = await VisibleExpense(expenseId, ct);

        if (!await StillInTheGroup(expense, ct))
            throw new ConflictException(ErrorCodes.TransactionGroupLeft,
                "You are no longer in this expense's group, so its bill cannot be changed.");

        return expense;
    }

    /// <summary>
    /// Whether the caller is still in the expense's group -- vacuously true for a personal
    /// one, which is nobody's but theirs.
    /// </summary>
    /// <remarks>
    /// Shared by the refusal and by the response that predicts it, so the two cannot drift:
    /// a flag saying a division would work, computed differently from the check that decides
    /// whether it does, is a flag that is eventually wrong.
    /// </remarks>
    private async Task<bool> StillInTheGroup(Expense expense, CancellationToken ct)
    {
        if (expense.GroupId is not { } groupId)
            return true;

        return await dbContext.Entry(userContext.User).Collection(user => user.Groups)
            .Query()
            .AnyAsync(@group => @group.Id == groupId, ct);
    }

}

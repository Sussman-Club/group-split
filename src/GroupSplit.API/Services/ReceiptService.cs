using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IReceiptService
{
    /// <summary>The bill on that expense.</summary>
    Task<Receipt> ForExpense(Guid expenseId, CancellationToken ct = default);

    /// <summary>The bill typed against that imported row, filed or not.</summary>
    Task<Receipt> ForBankRow(Guid bankTransactionId, CancellationToken ct = default);

    /// <summary>
    /// Stores the bill on an expense, replacing whatever was there. Does not divide it --
    /// see <see cref="Divide"/>, which is a separate act because a bill is usually written
    /// down before everybody has said what they had.
    /// </summary>
    Task<Receipt> SaveForExpense(Guid expenseId, SaveReceiptRequest request, CancellationToken ct = default);

    /// <summary>
    /// Stores the bill against an imported row that nobody has filed yet, so it can be
    /// itemised at the table and carried over when it is filed.
    /// </summary>
    Task<Receipt> SaveForBankRow(Guid bankTransactionId, SaveReceiptRequest request,
        CancellationToken ct = default);

    /// <summary>Replaces who had one line.</summary>
    Task<Receipt> SetClaims(Guid expenseId, Guid itemId, SetReceiptItemClaimsRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// What dividing the bill would come to, without storing any of it.
    /// </summary>
    Task<ReceiptDivisionResponse> Preview(Guid expenseId, CancellationToken ct = default);

    /// <summary>
    /// Divides the expense by its bill and stores the shares.
    /// </summary>
    Task<Expense> Divide(Guid expenseId, CancellationToken ct = default);

    /// <summary>
    /// One bill, shaped for the wire, read as one purchase on it.
    /// </summary>
    /// <param name="expenseId">
    /// Which part of the bill is being read, or null to read the whole paper without
    /// answering for any one purchase on it -- which is what an unfiled bank row gets.
    /// </param>
    /// <remarks>
    /// Here rather than on the endpoint, because the response says whether the part could be
    /// divided and that depends on who may be given a share -- which is the group's
    /// participants, and the endpoint has no way to ask.
    /// </remarks>
    Task<ReceiptResponse> ResponseFor(
        Receipt receipt, Guid? expenseId = null, CancellationToken ct = default);

    /// <summary>
    /// Takes a bill off an imported row that nobody has filed.
    /// </summary>
    /// <remarks>
    /// The way out for a bill filing declined to take -- a row filed into a personal expense
    /// keeps its bill, and without this there would be nothing to do with it but overwrite
    /// it forever.
    /// </remarks>
    Task DeleteForBankRow(Guid bankTransactionId, CancellationToken ct = default);

    /// <summary>Takes the bill off an expense. The expense's shares are left as they are.</summary>
    Task DeleteForExpense(Guid expenseId, CancellationToken ct = default);

    /// <summary>
    /// The bill typed against a bank row, ready to be attached to the expense it is about to
    /// be filed as. Null when the row has none, and null when
    /// <paramref name="groupId"/> is -- a personal expense is shared with nobody, so a bill
    /// on one could never be divided, and the bill stays on the row rather than being taken
    /// somewhere it cannot be used. Called only by the inbox, when a row is filed into a new
    /// expense or linked to one that already exists.
    /// </summary>
    /// <remarks>
    /// Handed over rather than re-pointed here, because the expense does not exist yet:
    /// <c>TransactionService.Create</c> builds it, attaches this, and only then divides it --
    /// which is the order an itemised rule needs, since it divides by the bill.
    /// </remarks>
    Task<Receipt?> ForFiling(Guid bankTransactionId, Guid? groupId, CancellationToken ct = default);
}

/// <summary>
/// The itemised bill behind a payment: storing it, saying who had what, and turning that
/// into what each person owes.
/// </summary>
/// <remarks>
/// Storing a bill and dividing by it are deliberately two acts. A receipt is written down in
/// one go -- scanned, or typed at the table -- and then claimed line by line by however many
/// people are at dinner, which takes as long as it takes. Dividing in the same call would
/// mean either refusing every bill that is not fully claimed the moment it is transcribed, or
/// storing a division that is wrong until the last person taps.
/// <para>
/// The division itself goes through <see cref="IExpenseSplitter"/>'s stated-splits path,
/// exactly as amounts somebody typed do, which is the decision the whole feature rests on. An
/// itemised split is <em>not</em> a <see cref="SplitRuleVersion"/>: a rule is a division the
/// group has named and can point a category at, and it carries its own division so that the
/// row an expense points at says the same thing tomorrow as it did when the expense was
/// written. Claims are none of that -- they are one bill's, they change while people are
/// still working out who had what, and no second expense could ever be filed under them. So
/// the expense's <see cref="Transaction.SplitRuleVersion"/> is left null and the receipt is
/// the record of where the amounts came from.
/// </para>
/// </remarks>
public class ReceiptService(
    AppDbContext dbContext,
    ICurrentUser userContext,
    IGroupParticipants participants,
    IExpenseSplitter splitter) : IReceiptService
{
    public async Task<Receipt> ForExpense(Guid expenseId, CancellationToken ct = default)
    {
        await VisibleExpense(expenseId, ct);

        // Through the lines rather than through the receipt, because a bill is not an
        // expense: one warehouse charge can be two purchases, and the receipt that answers
        // here is the whole piece of paper, of which this expense is one part.
        return await Loaded()
                   .FirstOrDefaultAsync(
                       receipt => receipt.Items.Any(item => item.ExpenseId == expenseId), ct)
               ?? throw new NotFoundException(ErrorCodes.ReceiptNotFound,
                   "This expense has no itemised bill.");
    }

    public async Task<ReceiptResponse> ResponseFor(
        Receipt receipt, Guid? expenseId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // A bill read on its own -- an unfiled row, or the whole paper rather than one part
        // of it -- has no expense to answer for, so it reports its lines and nothing about
        // dividing them.
        if (expenseId is not { } id)
            return receipt.ToResponse(null, canDivide: false);

        var expense = await dbContext.Set<Expense>()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);

        if (expense is null)
            return receipt.ToResponse(id, canDivide: false);

        var participants = await ParticipantsFor(expense, ct);
        var claimants = ReceiptSplitCalculator.PartOf(receipt, id)
            .SelectMany(item => item.Claims)
            .Select(claim => claim.UserId)
            .Distinct();

        // Everything that has to hold for a division to go through, asked in one place
        // because every one of them was once a yes on this flag and a refusal one call
        // later, on a button this answer had lit up:
        var canDivide =
            // the expense is shared with somebody -- a bill on a personal one divides
            // between nobody;
            expense.GroupId is not null
            // the part is still this expense's money, which an edit to the amount or a link
            // to a row of a different figure can undo;
            && ReceiptSplitCalculator.PartAmounts(receipt).GetValueOrDefault(id) == expense.Amount
            // every claimant is still somebody the group can be divided between;
            && claimants.All(participants.Contains)
            // the bill adds up and none of this part's lines is unspoken for;
            && ReceiptSplitCalculator.CanDivide(receipt, id)
            // and the person reading may write to it at all. Reading reaches further than
            // writing: the payer goes on seeing an expense after they leave its group, and
            // MineToChange refuses them.
            && await StillInTheGroup(expense, ct);

        return receipt.ToResponse(id, canDivide);
    }

    public async Task DeleteForBankRow(Guid bankTransactionId, CancellationToken ct = default)
    {
        await OwnedBankRow(bankTransactionId, ct);

        var receipt = await ForBankRow(bankTransactionId, ct);

        RefuseIfItsLinesAreSomebodysPurchase(receipt,
            "This charge has already been added as an expense, and removing its bill would "
            + "take away what those expenses were divided by. Delete the expense instead.");

        dbContext.Remove(receipt);

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Refuses to rewrite or remove a bill whose lines are already somebody's purchases.
    /// </summary>
    /// <remarks>
    /// A bill stays on its bank row after the charge is filed, which is what lets one charge
    /// be several expenses -- and it means the row's own routes go on reaching a receipt that
    /// expenses are now divided by. Rewriting one replaces every line, so the expenses lose
    /// the lines that are their money and report no bill at all; removing one takes the lines
    /// and the claims with it, and an itemised rule then refuses to divide anything.
    /// <para>
    /// Neither is visible as a mistake afterwards: the balances do not move, they simply stop
    /// being explicable. So the row's routes are for a bill nobody has filed from, and a bill
    /// that has been filed from is changed through the expense that holds its lines.
    /// </para>
    /// </remarks>
    private static void RefuseIfItsLinesAreSomebodysPurchase(Receipt? receipt, string why)
    {
        if (receipt is null || !receipt.Items.Any(line => line.ExpenseId is not null))
            return;

        throw new ConflictException(ErrorCodes.BankTransactionAlreadyFiled, why)
            .WithExtension("transactionIds",
                receipt.Items.Where(line => line.ExpenseId is not null)
                    .Select(line => line.ExpenseId!.Value).Distinct().ToList());
    }

    public async Task<Receipt> ForBankRow(Guid bankTransactionId, CancellationToken ct = default)
    {
        await OwnedBankRow(bankTransactionId, ct);

        return await Loaded()
                   .FirstOrDefaultAsync(receipt => receipt.BankTransactionId == bankTransactionId, ct)
               ?? throw new NotFoundException(ErrorCodes.ReceiptNotFound,
                   "This imported row has no itemised bill.");
    }

    public async Task<Receipt> SaveForExpense(
        Guid expenseId, SaveReceiptRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expense = await MineToChange(expenseId, ct);

        // A bill exists to divide something between people, and a personal expense has
        // nobody to divide with -- the splitter refuses a stated split on one by name. Left
        // to be discovered at division time it was worse than a refusal: the receipt stored
        // fine, the response reported canDivide, and the button the API itself lit up
        // answered 422.
        if ((expense.Group?.Id ?? expense.GroupId) is null)
            throw new ValidationException(ErrorCodes.SplitOnAPersonalExpense,
                "A personal expense is not shared with anybody, so there is nothing to divide " +
                "its bill between.");

        // The bill has to be the expense's own money. Checked here rather than only at
        // division time, because a receipt whose total is not the amount would divide into
        // shares that cannot be stored, and the refusal for that names the shares rather
        // than the mistake.
        if (request.Total != expense.Amount)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"The receipt's total is {request.Total}, but the expense is {expense.Amount}.")
                .WithExtension("total", request.Total)
                .WithExtension("amount", expense.Amount);

        var existing = await Loaded()
            .FirstOrDefaultAsync(row => row.Items.Any(item => item.ExpenseId == expenseId), ct);

        var receipt = existing ?? new Receipt
        {
            Subtotal = request.Subtotal,
            Total = request.Total
        };

        // Every line belongs to this expense, which is what "the bill for this expense"
        // means: one purchase, one part, the whole paper. A charge that is two purchases is
        // split from the inbox instead, where there is a row to split.
        Apply(receipt, request, await ParticipantsFor(expense, ct), expenseId);

        if (existing is null)
            dbContext.Add(receipt);

        await dbContext.SaveChangesAsync(ct);

        return await ForExpense(expenseId, ct);
    }

    public async Task<Receipt> SaveForBankRow(
        Guid bankTransactionId, SaveReceiptRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = await OwnedBankRow(bankTransactionId, ct);

        if (request.Total != row.Amount)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"The receipt's total is {request.Total}, but the imported row is {row.Amount}.")
                .WithExtension("total", request.Total)
                .WithExtension("amount", row.Amount);

        var existing = await Loaded()
            .FirstOrDefaultAsync(receipt => receipt.BankTransactionId == bankTransactionId, ct);

        RefuseIfItsLinesAreSomebodysPurchase(existing,
            "This charge has already been added as an expense, so its bill is what those "
            + "expenses were divided by. Change the bill through the expense, or delete the "
            + "expense first.");

        var receipt = existing ?? new Receipt
        {
            BankTransactionId = bankTransactionId,
            Subtotal = request.Subtotal,
            Total = request.Total
        };

        // Nobody to check the claims against yet. An unfiled row belongs to one person and
        // has no group, so which people may appear on it is not knowable until it is filed
        // into one -- and filing re-checks, which is where a claim naming somebody who is not
        // in the destination is caught.
        Apply(receipt, request, known: null, expenseId: null);

        if (existing is null)
            dbContext.Add(receipt);

        await dbContext.SaveChangesAsync(ct);

        return await ForBankRow(bankTransactionId, ct);
    }

    public async Task<Receipt> SetClaims(
        Guid expenseId, Guid itemId, SetReceiptItemClaimsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expense = await MineToChange(expenseId, ct);
        var receipt = await ForExpense(expenseId, ct);

        var item = receipt.Items.FirstOrDefault(line => line.Id == itemId)
                   ?? throw new NotFoundException(ErrorCodes.ReceiptItemNotFound,
                       "That line is not on this receipt.");

        RefuseStrangeClaims(request.Claims, await ParticipantsFor(expense, ct), item.Name);

        item.Division = (ReceiptItemDivision)request.Split;

        dbContext.RemoveRange(item.Claims);
        item.Claims.Clear();

        // Only a claimed line keeps claims. A line that is the table's names nobody, so
        // anything sent alongside it would be stored and never read -- a division sitting in
        // the table that nothing applies.
        if (item.Division == ReceiptItemDivision.Claimed)
        {
            foreach (var claim in request.Claims)
                Attach(item, claim);
        }

        await dbContext.SaveChangesAsync(ct);

        return await ForExpense(expenseId, ct);
    }

    public async Task<ReceiptDivisionResponse> Preview(Guid expenseId, CancellationToken ct = default)
    {
        var expense = await VisibleExpense(expenseId, ct);
        var receipt = await ForExpense(expenseId, ct);

        var participants = await ParticipantsFor(expense, ct);

        var shares = ReceiptSplitCalculator.Divide(receipt, expense.Id, expense.Payer, participants);
        var claimed = ReceiptSplitCalculator.ClaimedSubtotals(
            ReceiptSplitCalculator.PartOf(receipt, expense.Id), expense.Payer, participants);

        return new ReceiptDivisionResponse(
            receipt.Id,
            receipt.Total,
            [
                .. shares.Select(share => new ReceiptShareResponse(
                    share.UserId,
                    // Rounded only for display. The division used the unrounded figure, which
                    // is why these need not sum to the subtotal and the shares always sum to
                    // the total.
                    decimal.Round(claimed.GetValueOrDefault(share.UserId), 2),
                    share.Amount))
            ]);
    }

    public async Task<Expense> Divide(Guid expenseId, CancellationToken ct = default)
    {
        var expense = await MineToChange(expenseId, ct);
        var receipt = await ForExpense(expenseId, ct);

        // Everything goes through the splitter, and never through TransactionSplit rows
        // written here: it is the one place a division is checked against the amount it
        // claims to divide, and the one place that knows how to attach a split to a tracked
        // expense. Both would otherwise be a second, quietly different copy.
        if (await splitter.DividesByItsBill(expense, ct))
        {
            // The expense is already filed under an itemised rule, so the ordinary rule path
            // produces exactly these shares *and* records which version produced them. Asking
            // for the division explicitly should not cost the expense its provenance, which
            // is what stating the amounts would do -- stated shares have no rule behind them
            // by definition, and clearing the version is how the splitter says so.
            expense.Bill = receipt;

            await splitter.WriteSplitsAsync(expense, ct);
        }
        else
        {
            // The same check the itemised handler makes on its own path, because a bill can
            // stop describing its expense here too: SaveForExpense pins them together, and
            // then a PATCH moves the amount, or a bank row is linked to an expense recorded
            // for a different figure. Without it the refusal comes from the sum guard in
            // Replace and blames shares nobody typed -- "the shares add up to 100.00, but the
            // expense is 200.00" -- with no hint a receipt is involved.
            RefuseIfTheBillIsNotThisExpense(receipt, expense);

            // No itemised rule to credit -- an expense under no category, or under one that
            // divides some other way, being divided by its bill this once. Stated amounts are
            // the honest record of that: a person decided it, and no rule will reproduce it.
            var shares = ReceiptSplitCalculator.Divide(
                receipt, expense.Id, expense.Payer, await ParticipantsFor(expense, ct));

            await splitter.WriteSplitsAsync(
                expense,
                [
                    .. shares.Select(share =>
                        new SplitInput { UserId = share.UserId, Amount = share.Amount })
                ],
                ct);
        }

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    public async Task DeleteForExpense(Guid expenseId, CancellationToken ct = default)
    {
        await MineToChange(expenseId, ct);

        var receipt = await ForExpense(expenseId, ct);

        // This expense's lines come off the bill; the bill itself may well outlive them,
        // because the same charge may be somebody else's groceries too. Only when nothing is
        // left of it -- no line naming any expense, and no bank row behind it -- is there a
        // receipt with nowhere to be, and then it goes.
        var mine = receipt.Items.Where(item => item.ExpenseId == expenseId).ToList();

        foreach (var item in mine)
        {
            dbContext.RemoveRange(item.Claims);
            dbContext.Remove(item);
            receipt.Items.Remove(item);
        }

        var orphaned = receipt.BankTransactionId is null
                       && receipt.Items.All(item => item.ExpenseId is null);

        if (orphaned)
            dbContext.Remove(receipt);

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<Receipt?> ForFiling(
        Guid bankTransactionId, Guid? groupId, CancellationToken ct = default)
    {
        // Nowhere to hand it to. A personal expense is shared with nobody, so a bill on one
        // could never be divided -- SaveForExpense refuses to attach one for exactly that
        // reason, and filing must not be the door that gets round it.
        //
        // Declined rather than pruned, which is what this did and what made it worse than
        // useless: with no group there are no allowed claimants, so every claim on the bill
        // counted as a stranger and was deleted -- then committed by the save at the end of
        // filing. Somebody who itemised five lines at the table and filed the row without
        // picking a group lost all of it, silently. The bill now simply stays on the row.
        if (groupId is not { } id)
            return null;

        var receipt = await Loaded()
            .FirstOrDefaultAsync(row => row.BankTransactionId == bankTransactionId, ct);

        if (receipt is null)
            return null;

        // Claims made before the row was filed named whoever the person picking them knew
        // about; the group it is filed into may not contain all of them. Dropped rather than
        // refused, because refusing would make an unfileable row out of a bill that is merely
        // ahead of itself, and the lines they were on go back to unclaimed -- which the
        // division refuses until somebody says who had them.
        //
        // Dropped here rather than left to ExpenseSplitter, which would refuse the whole
        // filing with SPLIT_USER_NOT_IN_GROUP: that check is right for a division somebody
        // asked for and wrong for one carried over from before the group was chosen.
        var allowed = await participants.IdsOf(id, ct);

        foreach (var item in receipt.Items)
        {
            var strangers = item.Claims.Where(claim => !allowed.Contains(claim.UserId)).ToList();

            if (strangers.Count == 0)
                continue;

            dbContext.RemoveRange(strangers);

            foreach (var stranger in strangers)
                item.Claims.Remove(stranger);
        }

        return receipt;
    }

    /// <summary>
    /// Refuses a bill that has stopped being the money its expense is.
    /// </summary>
    /// <remarks>
    /// Kept beside the division rather than only at save time, because the two can drift
    /// afterwards and neither of the things that moves them is a receipt operation: editing
    /// the amount, and linking an imported row to an expense recorded for a different figure.
    /// </remarks>
    private static void RefuseIfTheBillIsNotThisExpense(Receipt receipt, Expense expense)
    {
        // This expense's part of the bill, not the whole paper: a split charge is several
        // expenses, and each one answers only for the lines that name it.
        var part = ReceiptSplitCalculator.PartAmounts(receipt).GetValueOrDefault(expense.Id);

        if (part == expense.Amount)
            return;

        throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                $"This expense's part of the bill comes to {part} and the expense is " +
                $"{expense.Amount}. Update the bill to match, or take these lines off it and " +
                "divide the expense some other way.")
            .WithExtension("partTotal", part)
            .WithExtension("amount", expense.Amount);
    }

    /// <summary>
    /// Writes the request onto the receipt: its figures, its lines, and who had them.
    /// </summary>
    /// <remarks>
    /// Lines are matched by the id the client sent back, which is what keeps claims attached
    /// across a correction. A line whose id is not recognised is a new line and gets a new
    /// one; a stored line the request no longer mentions is gone, and its claims go with it.
    /// </remarks>
    private void Apply(
        Receipt receipt, SaveReceiptRequest request, IReadOnlyCollection<Guid>? known,
        Guid? expenseId)
    {
        receipt.Subtotal = request.Subtotal;
        receipt.Tax = request.Tax;
        receipt.Tip = request.Tip;
        receipt.Total = request.Total;

        if (request.Items.Count == 0)
            throw new ValidationException(ErrorCodes.ReceiptInvalid,
                "A receipt needs at least one item.");

        foreach (var line in request.Items)
        {
            if (line.TotalPrice < 0)
                throw new ValidationException(ErrorCodes.ReceiptInvalid,
                    $"\"{line.Name}\" has a negative price. A refunded line is a refund, not a bill.");

            RefuseStrangeClaims(line.Claims, known, line.Name);
        }

        var sent = request.Items.Select(line => line.Id).Where(id => id is not null).ToHashSet();

        var dropped = receipt.Items.Where(item => !sent.Contains(item.Id)).ToList();

        foreach (var item in dropped)
        {
            dbContext.RemoveRange(item.Claims);
            dbContext.Remove(item);
            receipt.Items.Remove(item);
        }

        var position = 0;

        foreach (var line in request.Items)
        {
            var item = receipt.Items.FirstOrDefault(stored => stored.Id == line.Id);

            if (item is null)
            {
                item = new ReceiptItem
                {
                    ReceiptId = receipt.Id,
                    ExpenseId = expenseId,
                    Name = line.Name.Trim(),
                    NormalizedName = Folded(line.Name),
                    TotalPrice = line.TotalPrice
                };

                receipt.Items.Add(item);

                // Ids are client-generated here as everywhere, so EF cannot tell a new row
                // from an existing one by its key -- a fresh item on a tracked receipt would
                // be taken for an UPDATE of a row that is not there. The same trap
                // ExpenseSplitter.Replace documents.
                if (dbContext.Entry(receipt).State is not EntityState.Detached)
                    dbContext.Entry(item).State = EntityState.Added;
            }
            else
            {
                item.Name = line.Name.Trim();
                item.NormalizedName = Folded(line.Name);
                item.TotalPrice = line.TotalPrice;
            }

            // The order the bill was sent in, which is the order it was read off the paper.
            item.Position = position++;

            item.UnitPrice = line.UnitPrice;
            item.Quantity = line.Quantity;
            item.IsTaxable = line.IsTaxable;
            item.Division = (ReceiptItemDivision)line.Split;

            // Which purchase the line is part of. Stated by the caller only when the whole
            // bill is one expense's; a bill typed against a bank row leaves it open until
            // somebody splits the charge.
            item.ExpenseId = expenseId;

            // A line that no longer divides by its claims should not keep them: they would
            // be invisible in the response and come back the moment somebody set it to
            // Claimed again, which is a division nobody asked for reappearing on its own.
            if (item.Division != ReceiptItemDivision.Claimed && item.Claims.Count > 0)
            {
                dbContext.RemoveRange(item.Claims);
                item.Claims.Clear();
            }

            // Claims are only replaced when the request says something about them, so a
            // client re-sending a bill to fix a price does not silently un-claim every line.
            if (line.Claims.Count == 0)
                continue;

            dbContext.RemoveRange(item.Claims);
            item.Claims.Clear();

            foreach (var claim in line.Claims)
                Attach(item, claim);
        }

        ReceiptSplitCalculator.RefuseIfFiguresDisagree(receipt);
    }

    /// <summary>
    /// A line's name folded for matching, so two of the same thing are recognisable as such.
    /// The same folding the merchant resolver uses.
    /// </summary>
    private static string Folded(string name) => name.Trim().ToLowerInvariant();

    private void Attach(ReceiptItem item, ReceiptClaimInput claim)
    {
        var row = new ReceiptItemClaim
        {
            ReceiptItemId = item.Id,
            UserId = claim.UserId,
            Weight = claim.Weight
        };

        item.Claims.Add(row);

        if (dbContext.Entry(item).State is not EntityState.Detached)
            dbContext.Entry(row).State = EntityState.Added;
    }

    /// <summary>
    /// Refuses claims that name somebody twice, or somebody the expense cannot be divided
    /// with.
    /// </summary>
    /// <param name="known">
    /// Who may appear, or null when there is nobody to check against -- a bill on a bank row
    /// nobody has filed. Filing checks them.
    /// </param>
    private static void RefuseStrangeClaims(
        IReadOnlyList<ReceiptClaimInput> claims, IReadOnlyCollection<Guid>? known, string itemName)
    {
        if (claims.Select(claim => claim.UserId).Distinct().Count() != claims.Count)
            throw new ValidationException(ErrorCodes.ReceiptInvalid,
                $"\"{itemName}\" names somebody twice. One claim per person per line -- " +
                "somebody who had more of it has a larger weight, not a second claim.");

        if (known is not null && claims.Any(claim => !known.Contains(claim.UserId)))
            throw new ConflictException(ErrorCodes.SplitUserNotInGroup,
                $"\"{itemName}\" is claimed by somebody who is neither a member of the group " +
                "nor invited to it.");
    }

    /// <summary>
    /// Everybody the expense may be divided between -- the group's participants, or just the
    /// payer when it is personal. The same set <c>ExpenseSplitter</c> checks stated splits
    /// against, so a bill that passes here cannot fail there.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> ParticipantsFor(Expense expense, CancellationToken ct)
    {
        var groupId = expense.Group?.Id ?? expense.GroupId;

        return groupId is null
            ? [expense.UserId]
            : await participants.IdsOf(groupId.Value, ct);
    }

    /// <summary>
    /// Receipts loaded the way a division needs them: every line, every claim on it, and the
    /// expense whose money it has to be.
    /// </summary>
    /// <remarks>
    /// The expense is here for <c>ReceiptResponse.CanDivide</c>, which answered yes for a
    /// bill that no longer matched its expense -- the one state where the answer costs
    /// somebody a refusal on a button the API lit up itself. Belt and braces rather than
    /// load-bearing: every caller has already tracked the expense, so EF's fixup would fill
    /// the navigation anyway. Stated so that a future caller which has not does not silently
    /// get a false answer.
    /// </remarks>
    /// <summary>
    /// A receipt with its lines in the order they are on the paper, and their claims.
    /// </summary>
    /// <remarks>
    /// Ordered here as well as in the projection, because the loaded graph is what the
    /// division reads: the leftover cent of an apportioning goes to one named holder, and
    /// which one is settled by a scan over these. An order the database chose would have made
    /// that cent move between two people for no reason anybody could see.
    /// </remarks>
    private IQueryable<Receipt> Loaded() =>
        dbContext.Set<Receipt>()
            .Include(receipt => receipt.Items.OrderBy(item => item.Position).ThenBy(item => item.Id))
            .ThenInclude(item => item.Claims);

    /// <summary>
    /// The expense, if the caller may see it: one in a group they are in, or one of their
    /// own.
    /// </summary>
    /// <remarks>
    /// The shares are included, and that is not an optimisation.
    /// <c>ExpenseSplitter.Replace</c> works out what to delete by reading
    /// <see cref="Transaction.Splits"/>, so an expense that arrives without them looks like
    /// one that has none: the old rows are left in the table and the new ones inserted
    /// beside them. The unique index on (transaction, user) turns that into a 500 where the
    /// payer recurs, and into an expense whose shares sum to twice its amount where they do
    /// not. Every write path in <c>TransactionService</c> includes them for the same reason.
    /// </remarks>
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

    /// <summary>
    /// The imported row, if it is the caller's own. Bank data is a person's and is never
    /// scoped to a group.
    /// </summary>
    private async Task<BankTransaction> OwnedBankRow(Guid id, CancellationToken ct) =>
        await dbContext.Set<BankTransaction>()
            .FirstOrDefaultAsync(row =>
                row.Id == id &&
                row.Account.Connection.UserId == userContext.User.Id &&
                row.Status != BankTransactionStatus.Superseded, ct)
        ?? throw new NotFoundException(ErrorCodes.BankTransactionNotFound,
            "Imported transaction not found.");
}

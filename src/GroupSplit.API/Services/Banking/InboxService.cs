using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

public interface IInboxService
{
    /// <summary>
    /// The imported rows the caller may see, filtered by what has been done with them.
    /// Superseded rows are never among them.
    /// </summary>
    Task<IQueryable<BankTransaction>> List(InboxFilter? filter, CancellationToken ct = default);

    /// <summary>How many rows are waiting, for the badge.</summary>
    /// <summary>
    /// How many rows are waiting, and -- when asked -- how many of those look like an
    /// expense already recorded.
    /// </summary>
    /// <param name="withDuplicates">
    /// Whether to run the matcher over the waiting rows as well as counting them. Off by
    /// default because the nav badge reads this on every page and only needs the count.
    /// </param>
    /// <remarks>
    /// The duplicate count is of rows carrying a <see cref="MatchConfidence.Confident"/>
    /// match, not of rows carrying any. A screen or a badge that says some of these may
    /// already be recorded should be right about it, and a possible match is not that claim
    /// -- it is a question asked of whoever files the row.
    /// </remarks>
    Task<InboxSummaryResponse> Summary(bool withDuplicates = false, CancellationToken ct = default);

    /// <summary>
    /// Turns a row into an expense: copies the bank's facts, applies the category's
    /// division, and links the two.
    /// </summary>
    Task<Expense> File(Guid id, FileBankTransactionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Expenses already recorded that this row could be, best first. A suggestion, and
    /// nothing happens on the strength of it.
    /// </summary>
    Task<IReadOnlyList<DuplicateMatch>> Matches(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The same question for several rows at once, keyed by row id, for a listing that has
    /// to say it on every row it shows.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DuplicateMatch>>> Matches(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Files one charge as several expenses, by saying which lines of its bill belong to
    /// which purchase.
    /// </summary>
    /// <remarks>
    /// For the charge that is two purchases -- the flat's groceries and a jacket of your own
    /// on one warehouse receipt. Filing it whole would put your clothes in the group's ledger
    /// and file them under Groceries; this gives each part its own expense, its own group and
    /// its own category, while the money stays one charge.
    /// <para>
    /// Everything at once. Every line lands in a part, so there is no half-split state to
    /// leave a row in and nothing to reconcile afterwards.
    /// </para>
    /// </remarks>
    Task<SplitBankTransactionResponse> Split(Guid id, SplitBankTransactionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// What each proposed part of a charge would come to, while somebody is still deciding.
    /// </summary>
    /// <remarks>
    /// The same apportioning <see cref="Split"/> uses, asked without creating anything -- so
    /// the figures a screen shows are the ones filing would store, rather than a second copy
    /// of the arithmetic that could drift from this one.
    /// </remarks>
    Task<SplitChargePreviewResponse> PreviewSplit(Guid id, SplitChargePreviewRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Attaches the row to an expense that is already there, instead of making a second one.
    /// </summary>
    Task<Expense> Link(Guid id, LinkBankTransactionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Records that the row and that expense are not the same money, so the pair is not
    /// suggested again.
    /// </summary>
    Task DismissMatch(Guid id, DismissBankMatchRequest request, CancellationToken ct = default);

    /// <summary>Takes a row out of the inbox without making anything of it.</summary>
    Task Ignore(Guid id, CancellationToken ct = default);

    /// <summary>Puts an ignored row back.</summary>
    Task Restore(Guid id, CancellationToken ct = default);
}

/// <summary>
/// What a person does with the rows a sync brought in.
/// </summary>
/// <remarks>
/// Filing goes through <see cref="ITransactionService.Create"/> rather than building an
/// expense here, so the category default, the even fallback, the membership checks and the
/// splits-must-sum rule are the ones a typed expense already meets. What this adds is the
/// link back and the row's new status, and the guards that only make sense for imported
/// money: a credit is not an expense, a row already filed must not become a second one, and
/// a row that looks like an expense somebody has already recorded is not filed until they
/// have been told and have said which it is.
/// <para>
/// What counts as looking like one is <see cref="IDuplicateMatcher"/>'s and not this
/// class's. Everything here does with a match is raise it, act on the answer, or remember
/// that the answer was no.
/// </para>
/// </remarks>
public sealed class InboxService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    ITransactionService transactions,
    IDuplicateMatcher matcher,
    IReceiptService receipts) : IInboxService
{
    public Task<IQueryable<BankTransaction>> List(InboxFilter? filter, CancellationToken ct = default)
    {
        var wanted = Stored(filter?.Status ?? InboxStatus.New);

        // Hoisted out of the expression so the comparison is against a parameter rather than
        // a property read the provider would have to translate. Both ends are inclusive: a
        // person narrowing to a month means the whole month, last day included.
        var after = filter?.From;
        var before = filter?.To;

        // Against the authorized date where the provider knows it, which is the date the row
        // shows and therefore the one somebody is filtering by. A card charge often posts
        // days after it was spent, and matching on the posting date would put a row dated
        // the 30th of last month inside "this month" -- the row would then sit under a
        // heading naming a span its own date is outside of. InboxApi.Sort orders by the same
        // date for the same reason: what is filtered, ordered and shown has to be one date.
        return Task.FromResult(Owned().Where(row =>
            row.Status == wanted &&
            (after == null || (row.AuthorizedDate ?? row.Date) >= after) &&
            (before == null || (row.AuthorizedDate ?? row.Date) <= before)));
    }

    public async Task<InboxSummaryResponse> Summary(bool withDuplicates = false,
        CancellationToken ct = default)
    {
        var waiting = await Owned()
            .Where(row => row.Status == BankTransactionStatus.New)
            .ToListAsync(ct);

        if (!withDuplicates)
            return new InboxSummaryResponse(waiting.Count);

        var matches = await matcher.ExpensesLike(waiting, ct);

        return new InboxSummaryResponse(
            waiting.Count,
            matches.Count(row => row.Value.Any(match => match.Confidence == MatchConfidence.Confident)));
    }

    public async Task<Expense> File(Guid id, FileBankTransactionRequest request, CancellationToken ct = default)
    {
        var row = await Existing(id, ct);

        if (row.Status == BankTransactionStatus.Filed)
        {
            throw new ConflictException(ErrorCodes.BankTransactionAlreadyFiled,
                "This imported transaction has already been added as an expense.");
        }

        // Money coming in is not an expense, and there is no other kind of transaction yet.
        // Filing it as a negative expense would put a number every total has to know about
        // into the ledger.
        if (row.Amount < 0)
        {
            throw new UnprocessableException(ErrorCodes.BankTransactionIsCredit,
                    "This is money coming in, so it cannot be added as an expense.")
                .WithExtension("amount", row.Amount);
        }

        await RefuseCurrencyMismatch(row, request.GroupId, ct);

        // Before the expense exists, not after: a duplicate discovered afterwards is a
        // wrong balance somebody has to notice and undo. The refusal names what it matched,
        // and the person answers it -- by filing anyway, because they really did pay twice,
        // or by pointing the row at the expense that is already there through Link.
        //
        // Over either grade, deliberately. A tip added to a dinner is the likeliest
        // duplicate there is and it is only ever a possible match, so a guard that asked for
        // a confident one would let the commonest case through in silence. What the grade
        // decides is how loudly a suggestion is announced to somebody who has not touched
        // the row -- not whether the money can be recorded twice without a word.
        if (!request.FileAnyway)
        {
            var matches = await matcher.ExpensesLike(row, ct);

            if (matches.Count > 0)
            {
                throw new ConflictException(ErrorCodes.PossibleDuplicateExpense, Refusal(matches))
                    .WithExtension(ProblemDetails.MatchesExtension, matches.ToResponses());
            }
        }

        // Loaded before the expense is made, because Create divides it and an itemised rule
        // divides by this. Claims naming somebody who is not in the destination group are
        // dropped first -- the row belonged to one person and had no group to check against
        // when it was typed.
        var bill = await receipts.ForFiling(row.Id, request.GroupId, ct);

        var expense = await transactions.Create(new CreateTransactionRequest
        {
            GroupId = request.GroupId,
            CategoryId = request.CategoryId,
            PaidByUserId = request.PaidByUserId,
            Splits = request.Splits,
            Name = Named(request.Name, row),
            Description = request.Description,
            Amount = row.Amount,
            // A statement has a date and not an instant. Midnight UTC keeps it the day the
            // bank said, whichever zone it is later read in.
            DateTime = row.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        }, bill, ct: ct);

        expense.BankTransaction = row;
        expense.BankTransactionId = row.Id;
        // Where it was spent, carried onto the ledger. A link and not a name, so an expense
        // filed today shows the logo a provider only supplies next month.
        expense.MerchantId = row.MerchantId;
        row.Status = BankTransactionStatus.Filed;

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    /// <summary>
    /// How sure to sound about it. The same refusal carries both grades, and claiming the
    /// row *is* an expense already recorded when the amounts merely sit within a quarter of
    /// each other is how a person learns to stop reading the sentence.
    /// </summary>
    private static string Refusal(IReadOnlyList<DuplicateMatch> matches) =>
        matches.Any(match => match.Confidence == MatchConfidence.Confident)
            ? "This looks like an expense you have already recorded."
            : "This could be an expense you have already recorded.";

    public async Task<IReadOnlyList<DuplicateMatch>> Matches(Guid id, CancellationToken ct = default)
    {
        var row = await Existing(id, ct);

        return await matcher.ExpensesLike(row, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DuplicateMatch>>> Matches(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<DuplicateMatch>>();

        // Through the caller's own rows, like every other read here: ids that are not
        // theirs simply do not come back, and nothing says whether they exist.
        var rows = await Owned().Where(row => ids.Contains(row.Id)).ToListAsync(ct);

        return await matcher.ExpensesLike(rows, ct);
    }

    public async Task<Expense> Link(Guid id, LinkBankTransactionRequest request, CancellationToken ct = default)
    {
        var row = await Existing(id, ct);

        if (row.Status == BankTransactionStatus.Filed)
        {
            throw new ConflictException(ErrorCodes.BankTransactionAlreadyFiled,
                "This imported transaction has already been added as an expense.");
        }

        if (row.Amount < 0)
        {
            throw new UnprocessableException(ErrorCodes.BankTransactionIsCredit,
                    "This is money coming in, so it is not an expense to attach to one.")
                .WithExtension("amount", row.Amount);
        }

        var expense = await Mine(request.TransactionId, ct);

        if (expense.BankTransactionId is not null)
        {
            throw new ConflictException(ErrorCodes.TransactionAlreadyImported,
                "That expense already came from a bank transaction.");
        }

        if (!string.Equals(expense.Currency, row.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(ErrorCodes.CurrencyMismatch,
                    $"This is in {row.Currency} and that expense is in {expense.Currency}.")
                .WithExtension("transactionCurrency", row.Currency)
                .WithExtension("expenseCurrency", expense.Currency);
        }

        // The same link filing makes, and nothing else. The expense keeps the amount, the
        // date and the division it was recorded with: the person wrote those down, and the
        // bank settling for a different figure is not a correction anybody asked for.
        expense.BankTransaction = row;
        expense.BankTransactionId = row.Id;
        // The merchant comes across on a link too. Pointing a row at an expense somebody
        // typed in is how that expense learns where it happened -- the amount and the date
        // it keeps, because those were written down on purpose, but nobody typed a shop.
        expense.MerchantId = row.MerchantId;
        row.Status = BankTransactionStatus.Filed;

        // A bill typed against the row before anybody linked it follows the row onto the
        // expense -- but only when the expense has none of its own. Linking says these two
        // are the same money, not that the row's account of it replaces one somebody has
        // already written down, and a receipt has exactly one owner.
        //
        // It does not re-divide the expense either way. Linking changes where the money came
        // from and not who owed what; dividing by the bill is its own call, and the
        // category's rule can say to do it.
        // Asked before the bill is loaded, not after, and that order is the fix for a bug:
        // ForFiling prunes claims naming people who are not in the destination group, and
        // Link ends in a save -- so calling it and then declining to take the bill committed
        // the pruning to a receipt that stayed on the bank row, silently un-claiming lines
        // against a group it was never filed into.
        //
        // Asked of the table rather than of expense.Receipt, which Mine does not load: an
        // unloaded navigation reads null exactly like an expense that has no bill, and taking
        // that for permission would point a second receipt at it and break the check
        // constraint.
        var alreadyHasOne = await dbContext.Set<ReceiptItem>()
            .AnyAsync(item => item.ExpenseId == expense.Id, ct);

        if (!alreadyHasOne && await receipts.ForFiling(row.Id, expense.GroupId, ct) is { } bill)
        {
            foreach (var item in bill.Items)
                item.ExpenseId = expense.Id;
        }

        await dbContext.SaveChangesAsync(ct);

        return expense;
    }

    public async Task DismissMatch(Guid id, DismissBankMatchRequest request, CancellationToken ct = default)
    {
        var row = await Existing(id, ct);
        var expense = await Mine(request.TransactionId, ct);

        await matcher.Dismiss(row, expense, ct);
    }

    public async Task Ignore(Guid id, CancellationToken ct = default)
    {
        var row = await Existing(id, ct);

        if (row.Status == BankTransactionStatus.Filed)
        {
            throw new ConflictException(ErrorCodes.BankTransactionAlreadyFiled,
                "This one is already an expense. Delete the expense to undo that.");
        }

        row.Status = BankTransactionStatus.Ignored;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task Restore(Guid id, CancellationToken ct = default)
    {
        var row = await Existing(id, ct);

        if (row.Status == BankTransactionStatus.Ignored)
        {
            row.Status = BankTransactionStatus.New;
            await dbContext.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The rows belonging to the caller's own connections. Bank data is a person's, so this
    /// is not scoped by group and never widens to one.
    /// </summary>
    private IQueryable<BankTransaction> Owned() =>
        dbContext.Set<BankTransaction>()
            .Where(row => row.Account.Connection.UserId == userContext.User.Id
                          && row.Status != BankTransactionStatus.Superseded);

    private async Task<BankTransaction> Existing(Guid id, CancellationToken ct) =>
        await Owned().FirstOrDefaultAsync(row => row.Id == id, ct)
        // Somebody else's row, and a superseded one, take this path too: both say the same
        // thing, which is that it is not the caller's to act on.
        ?? throw new NotFoundException(ErrorCodes.BankTransactionNotFound, "Imported transaction not found.");

    /// <summary>
    /// The caller's own expense, by id: one they paid for, wherever it sits.
    /// </summary>
    /// <remarks>
    /// Narrower than what they may read. A bank row is one person's card charge, so the
    /// only expense it can be the same money as is one they are down as having paid --
    /// anything else is somebody else's, and a 404 says that the way the rest of the API
    /// says it.
    /// </remarks>
    private async Task<Expense> Mine(Guid transactionId, CancellationToken ct) =>
        await dbContext.Set<Expense>()
            .FirstOrDefaultAsync(expense => expense.Id == transactionId
                                            && expense.UserId == userContext.User.Id, ct)
        ?? throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

    /// <summary>
    /// A group's balances are one currency, and conversion is out of scope, so a row in
    /// another one cannot join them. Checked before the expense is created rather than
    /// after, and only for a group the caller is actually in -- otherwise the group's own
    /// 404 is the answer, and this must not leak that the group exists.
    /// </summary>
    private async Task RefuseCurrencyMismatch(BankTransaction row, Guid? groupId, CancellationToken ct)
    {
        if (groupId is not { } id)
            return;

        var currency = await dbContext.Entry(userContext.User).Collection(user => user.Groups).Query()
            .Where(group => group.Id == id)
            .Select(group => group.Currency)
            .FirstOrDefaultAsync(ct);

        if (currency is null || string.Equals(currency, row.Currency, StringComparison.OrdinalIgnoreCase))
            return;

        throw new ConflictException(ErrorCodes.CurrencyMismatch,
                $"This is in {row.Currency} and the group keeps its balances in {currency}.")
            .WithExtension("transactionCurrency", row.Currency)
            .WithExtension("groupCurrency", currency);
    }

    public async Task<SplitBankTransactionResponse> Split(
        Guid id, SplitBankTransactionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = await Existing(id, ct);

        if (row.Status == BankTransactionStatus.Filed)
        {
            throw new ConflictException(ErrorCodes.BankTransactionAlreadyFiled,
                "This imported transaction has already been added as an expense.");
        }

        if (row.Amount < 0)
        {
            throw new UnprocessableException(ErrorCodes.BankTransactionIsCredit,
                    "This is money coming in, so it cannot be added as an expense.")
                .WithExtension("amount", row.Amount);
        }

        // Once per destination: a split can land its parts in different groups, and each of
        // them keeps its balances in its own currency.
        foreach (var groupId in request.Parts.Select(part => part.GroupId).Distinct())
            await RefuseCurrencyMismatch(row, groupId, ct);

        // Before any of it exists, exactly as an ordinary filing does -- a duplicate found
        // afterwards is several wrong balances rather than one.
        if (!request.FileAnyway)
        {
            var matches = await matcher.ExpensesLike(row, ct);

            if (matches.Count > 0)
            {
                throw new ConflictException(ErrorCodes.PossibleDuplicateExpense, Refusal(matches))
                    .WithExtension(ProblemDetails.MatchesExtension, matches.ToResponses());
            }
        }

        // There is nothing to split without one. A charge with no bill is filed whole, which
        // is what File is for.
        var bill = await receipts.ForBankRow(row.Id, ct);

        // Checked again here, not only when the bill was typed. A pending row's amount is the
        // bank's to change before it posts, so a bill typed at the table against 100.00 can
        // be facing a 105.00 charge by the time anybody splits it -- and the parts are cut
        // from the bill, so they would sum to the old figure while the response and the
        // ledger said the new one.
        if (bill.Total != row.Amount)
        {
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"This bill comes to {bill.Total} and the charge is {row.Amount}, so its " +
                    "parts cannot add up to what was paid. Update the bill to match the " +
                    "charge before splitting it.")
                .WithExtension("receiptTotal", bill.Total)
                .WithExtension("amount", row.Amount);
        }

        var parts = PartsOf(bill, request);

        // What each part comes to, worked out before any expense exists -- an expense cannot
        // be created without its amount, and the amount depends on which lines it holds.
        // Cut from the charge rather than added up towards it, so the parts sum to it.
        var amounts = ReceiptSplitCalculator.AmountsFor(bill, parts);

        // Every line told which purchase it is before any part is divided, rather than a part
        // at a time. A rule that divides by the bill re-derives its part from the receipt, and
        // a receipt half placed is a bill with half its lines: the tax gets apportioned over
        // the lines placed so far, and the first part of a taxed charge is handed a figure
        // that disagrees with the amount it was cut for. Which needs the ids up front, and
        // this is why they are made here.
        var ids = request.Parts.Select(_ => Guid.NewGuid()).ToList();

        // What the lines said before, so a failure can put them back. The expenses are only
        // tracked until the save below, but the lines are rows that already exist: left
        // pointing at expenses that were never created, they would be flushed by the next
        // thing to save in this scope. Nothing does today, and a bill quietly claiming to be
        // several purchases that do not exist is not worth resting on that.
        var wasPlaced = bill.Items.ToDictionary(line => line.Id, line => line.ExpenseId);

        var filed = new List<SplitPartResponse>();

        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                foreach (var line in parts[i])
                    line.ExpenseId = ids[i];
            }

            for (var i = 0; i < request.Parts.Count; i++)
            {
                var part = request.Parts[i];

                // Built and not saved. The whole split commits once, below, so a part that
                // cannot be built takes the parts before it with it -- rather than leaving
                // them in the ledger against a row that still reads as waiting, where the
                // obvious next move is to fix the bad part and split again, filing the good
                // ones a second time.
                var expense = await transactions.Build(new CreateTransactionRequest
                {
                    GroupId = part.GroupId,
                    CategoryId = part.CategoryId,
                    PaidByUserId = part.PaidByUserId,
                    Splits = part.Splits,
                    Name = Named(part.Name, row),
                    Description = part.Description,
                    Amount = amounts[i],
                    DateTime = row.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                }, bill, part.ItemIds, ids[i], ct);

                dbContext.Add(expense);

                // Every part came from this charge, and each says so. The link is many-to-one
                // now for exactly this reason.
                expense.BankTransaction = row;
                expense.BankTransactionId = row.Id;
                expense.MerchantId = row.MerchantId;

                filed.Add(new SplitPartResponse(
                    expense.Id, expense.Name, expense.GroupId, expense.Amount, part.ItemIds.Count));
            }
        }
        catch
        {
            foreach (var line in bill.Items)
                line.ExpenseId = wasPlaced[line.Id];

            throw;
        }

        row.Status = BankTransactionStatus.Filed;

        // Once, for the lot. EF wraps a single SaveChanges in one database transaction, which
        // is what makes this all-or-nothing rather than a run of separate filings.
        await dbContext.SaveChangesAsync(ct);

        return new SplitBankTransactionResponse(row.Id, row.Amount, filed);
    }

    public async Task<SplitChargePreviewResponse> PreviewSplit(
        Guid id, SplitChargePreviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = await Existing(id, ct);
        var bill = await receipts.ForBankRow(row.Id, ct);

        var byId = bill.Items.ToDictionary(item => item.Id);

        // Lines that are not this bill's are dropped rather than refused. This prices a
        // screen mid-edit, and the one thing it must not do is go blank: a stale id from a
        // bill that changed under somebody is worth pricing without, not worth a refusal
        // where there is no field to fix it in.
        var parts = request.Parts
            .Select(IReadOnlyList<ReceiptItem> (part) =>
                [.. part.ItemIds.Select(byId.GetValueOrDefault).OfType<ReceiptItem>()])
            .ToList();

        var amounts = ReceiptSplitCalculator.AmountsFor(bill, parts);

        // The row's amount rather than the bill's, so what is "still to place" is measured
        // against the money being divided. They are the same figure for a bill that can be
        // split at all; where they differ the screen says so by never reaching zero, which is
        // the honest thing for it to say -- Split refuses that bill by name.
        return new SplitChargePreviewResponse(amounts, row.Amount, amounts.Sum());
    }

    /// <summary>
    /// The bill's lines, grouped as the request asks -- and refused unless every line lands
    /// in exactly one part.
    /// </summary>
    /// <remarks>
    /// Not a tidiness check. A part's amount is cut from the charge in proportion to the
    /// lines it holds, so a line left out is money no part accounts for and the parts stop
    /// summing to what the card was charged; a line named twice is money counted twice. Both
    /// are refused here, by name, rather than surfacing later as a total nobody can explain.
    /// </remarks>
    private static List<IReadOnlyList<ReceiptItem>> PartsOf(
        Receipt bill, SplitBankTransactionRequest request)
    {
        var byId = bill.Items.ToDictionary(item => item.Id);
        var seen = new HashSet<Guid>();
        var twice = new List<Guid>();
        var strangers = new List<Guid>();
        var parts = new List<IReadOnlyList<ReceiptItem>>();

        foreach (var part in request.Parts)
        {
            var lines = new List<ReceiptItem>();

            foreach (var itemId in part.ItemIds)
            {
                if (!byId.TryGetValue(itemId, out var item))
                {
                    strangers.Add(itemId);
                    continue;
                }

                if (!seen.Add(itemId))
                {
                    twice.Add(itemId);
                    continue;
                }

                lines.Add(item);
            }

            parts.Add(lines);
        }

        if (strangers.Count > 0)
            throw new ValidationException(ErrorCodes.SplitPartsInvalid,
                    "A part names a line that is not on this bill.")
                .WithExtension("unknownItemIds", strangers);

        if (twice.Count > 0)
            throw new ValidationException(ErrorCodes.SplitPartsInvalid,
                    "A line of the bill is in more than one part. Each line belongs to one " +
                    "purchase.")
                .WithExtension("duplicatedItemIds", twice);

        var missed = bill.Items.Where(item => !seen.Contains(item.Id)).ToList();

        if (missed.Count > 0)
            throw new ValidationException(ErrorCodes.SplitPartsInvalid,
                    $"{missed.Count} line(s) of the bill are in no part. Every line has to " +
                    "belong to one before the charge can be filed.")
                .WithExtension("unplacedItemIds", missed.ConvertAll(item => item.Id))
                .WithExtension("unplacedItemNames", missed.ConvertAll(item => item.Name));

        return parts;
    }

    private static string Named(string? requested, BankTransaction row) =>
        string.IsNullOrWhiteSpace(requested)
            ? Clip(string.IsNullOrWhiteSpace(row.MerchantName) ? row.Description : row.MerchantName, 124)
            : requested;

    private static string Clip(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>The stored status a wire one asks for.</summary>
    private static BankTransactionStatus Stored(InboxStatus status) => status switch
    {
        InboxStatus.New => BankTransactionStatus.New,
        InboxStatus.Filed => BankTransactionStatus.Filed,
        InboxStatus.Ignored => BankTransactionStatus.Ignored,
        _ => BankTransactionStatus.New
    };
}

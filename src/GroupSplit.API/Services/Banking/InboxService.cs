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
    IDuplicateMatcher matcher) : IInboxService
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
        }, ct);

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

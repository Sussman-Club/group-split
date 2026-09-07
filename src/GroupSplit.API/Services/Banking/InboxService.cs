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
    Task<InboxSummaryResponse> Summary(CancellationToken ct = default);

    /// <summary>
    /// Turns a row into an expense: copies the bank's facts, applies the category's
    /// division, and links the two.
    /// </summary>
    Task<Expense> File(Guid id, FileBankTransactionRequest request, CancellationToken ct = default);

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
/// link back and the row's new status, and the two guards that only make sense for imported
/// money: a credit is not an expense, and a row already filed must not become a second one.
/// </remarks>
public sealed class InboxService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    ITransactionService transactions) : IInboxService
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
        // heading naming a span its own date is outside of.
        return Task.FromResult(Owned().Where(row =>
            row.Status == wanted &&
            (after == null || (row.AuthorizedDate ?? row.Date) >= after) &&
            (before == null || (row.AuthorizedDate ?? row.Date) <= before)));
    }

    public async Task<InboxSummaryResponse> Summary(CancellationToken ct = default)
    {
        var waiting = await Owned().CountAsync(row => row.Status == BankTransactionStatus.New, ct);

        return new InboxSummaryResponse(waiting);
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
        row.Status = BankTransactionStatus.Filed;

        await dbContext.SaveChangesAsync(ct);

        return expense;
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

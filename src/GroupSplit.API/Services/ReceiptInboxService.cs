using GroupSplit.API.Errors;
using GroupSplit.API.Services.Banking;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public sealed class ReceiptInboxService(AppDbContext db, ICurrentUser user, IAccountDataGuard guard,
    ITransactionService transactions, IInboxService bank, IReceiptFiles files)
{
    private IQueryable<Receipt> Owned() => db.Set<Receipt>().Where(r => r.OwnerUserId == user.User.Id);
    private async Task<Receipt> Find(Guid id, CancellationToken ct) =>
        await Owned().Include(r => r.Items).Include(r => r.Attachments).FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.ReceiptNotFound, "Receipt not found.");
    private static void Unfiled(Receipt r)
    {
        if (r.ExpenseId != null) throw new ConflictException(ErrorCodes.ReceiptAlreadyFiled, "This receipt is already filed.");
    }
    public async Task<IReadOnlyList<ReceiptInboxResponse>> List(CancellationToken ct) =>
        (await Owned().Where(r => r.ExpenseId == null).Include(r => r.Items).Include(r => r.Attachments)
            .OrderByDescending(r => r.PurchaseDate).ThenBy(r => r.Id).ToListAsync(ct)).Select(Response).ToList();
    public async Task<ReceiptInboxResponse> Get(Guid id, CancellationToken ct) => Response(await Find(id, ct));
    public async Task<ReceiptInboxResponse> Save(Guid id, SaveReceiptInboxRequest request, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        var r = await Owned().Include(r => r.Items).Include(r => r.Attachments).FirstOrDefaultAsync(r => r.Id == id, ct);
        var details = request.Receipt;
        var hasDetails = details is not null && (details.Items.Count > 0 ||
            !string.IsNullOrWhiteSpace(details.MerchantName) || details.PurchaseDate is not null ||
            details.Total != 0 || details.Subtotal != 0 || details.Tax != 0 || details.Tip != 0);
        if (!hasDetails && (r is null || r.Attachments.Count == 0 &&
            (details is not null || r.Items.Count == 0 && string.IsNullOrWhiteSpace(r.MerchantName) &&
             r.PurchaseDate is null && r.Total == 0 && r.Subtotal == 0 && r.Tax == 0 && r.Tip == 0)))
            throw new ValidationException(ErrorCodes.ReceiptInvalid, "Upload a receipt file or enter receipt details before saving.");
        if (r == null)
        {
            if (await db.Set<Receipt>().AnyAsync(r => r.Id == id, ct))
                throw new NotFoundException(ErrorCodes.ReceiptNotFound, "Receipt not found.");
            r = new Receipt { Id = id, OwnerUserId = user.User.Id, Subtotal = 0, Total = 0 };
            db.Add(r);
        }
        Unfiled(r);
        r.Currency = request.Currency.ToUpperInvariant();
        if (request.Receipt is { } input)
        {
            if (input.Items.Any(i => i.SplitRuleVersionId != null))
                throw new ValidationException(ErrorCodes.ReceiptInvalid, "Choose split rules after filing into a group.");
            r.MerchantName = input.MerchantName?.Trim(); r.PurchaseDate = input.PurchaseDate;
            r.Subtotal = input.Subtotal; r.Tax = input.Tax; r.Tip = input.Tip; r.Total = input.Total;
            db.RemoveRange(r.Items); r.Items.Clear();
            foreach (var line in input.Items)
                r.Items.Add(new ReceiptItem { Position = r.Items.Count, Name = line.Name,
                    NormalizedName = line.NormalizedName ?? line.Name.ToLowerInvariant(), Description = line.Description,
                    UnitPrice = line.UnitPrice, Quantity = line.Quantity, TotalPrice = line.TotalPrice,
                    TaxAmount = line.TaxAmount, TaxRate = line.TaxRate, IsTaxable = line.IsTaxable });
            db.AddRange(r.Items);
        }
        await db.SaveChangesAsync(ct);
        return Response(r);
    }
    public async Task Delete(Guid id, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        var r = await Find(id, ct); Unfiled(r);
        var keys = r.Attachments.Select(a => a.ObjectKey).ToArray();
        files.Enqueue(keys); db.RemoveRange(r.Attachments); db.Remove(r);
        await db.SaveChangesAsync(ct);
        await files.DeleteAsync(keys, "Could not delete private receipt source.", ct);
    }
    public async Task<IReadOnlyList<ReceiptExpenseCandidate>> Matches(Guid id, CancellationToken ct)
    {
        var r = await Find(id, ct);
        if (r.PurchaseDate is not { } day || r.Total == 0) return [];
        var start = new DateTimeOffset(day.AddDays(-7).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(15);
        var groups = db.Entry(user.User).Collection(u => u.Groups).Query();
        return await db.Set<Expense>().Where(e => (e.UserId == user.User.Id || groups.Any(g => g.Id == e.GroupId))
                && e.Currency == r.Currency && e.Amount == r.Total && e.DateTime >= start && e.DateTime < end)
            .OrderByDescending(e => e.DateTime).Take(20)
            .Select(e => new ReceiptExpenseCandidate(e.Id, e.Name, e.Amount, e.Currency, e.DateTime)).ToListAsync(ct);
    }
    public async Task<ReceiptInboxResponse> File(Guid id, FileReceiptInboxRequest request, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var r = await Find(id, ct);
        if (r.ExpenseId != null)
        {
            if (request.ExpenseId != null && request.ExpenseId != r.ExpenseId)
                throw new ConflictException(ErrorCodes.ReceiptAlreadyFiled, "This receipt was filed elsewhere.");
            return Response(r);
        }
        if (new[] { request.ExpenseId != null, request.BankTransactionId != null && request.ExpenseId == null, request.NewExpense != null }.Count(x => x) != 1)
            throw new ValidationException(ErrorCodes.ReceiptInvalid, "Choose one filing destination.");
        if (r.Items.Count > 0) { ReceiptSplitCalculator.CalculateLineTaxes(r); ReceiptSplitCalculator.RefuseIfFiguresDisagree(r); }
        Expense expense;
        if (request.ExpenseId is { } expenseId)
        {
            var groups = db.Entry(user.User).Collection(u => u.Groups).Query();
            expense = await db.Set<Expense>().FirstOrDefaultAsync(e => e.Id == expenseId && groups.Any(g => g.Id == e.GroupId), ct)
                ?? throw new NotFoundException(ErrorCodes.TransactionNotFound, "Expense not found.");
        }
        else if (request.BankTransactionId is { } bankId)
            expense = await bank.File(bankId, request.BankExpense ?? new(), ct);
        else
        {
            if (!request.CreateAnyway)
            {
                var candidates = await Matches(id, ct);
                if (candidates.Count > 0)
                {
                    var ids = candidates.Select(c => c.Id).ToArray();
                    var expenses = await db.Set<Expense>().Include(e => e.Group).Include(e => e.User)
                        .Where(e => ids.Contains(e.Id)).ToListAsync(ct);
                    var matches = expenses.Select(e => new DuplicateMatch(e, 0,
                        Math.Abs(DateOnly.FromDateTime(e.DateTime.UtcDateTime).DayNumber - r.PurchaseDate!.Value.DayNumber), MatchConfidence.Confident));
                    throw new ConflictException(ErrorCodes.PossibleDuplicateExpense,
                        "Review matching expenses before recording another purchase.")
                        .WithExtension(ProblemDetails.MatchesExtension, matches.ToResponses());
                }
            }
            var input = request.NewExpense!;
            if (!string.Equals(input.Currency ?? r.Currency, r.Currency, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException(ErrorCodes.CurrencyMismatch, "Receipt and expense currencies differ.")
                .WithExtension("receiptCurrency", r.Currency).WithExtension("expenseCurrency", input.Currency);
            input.Currency = r.Currency;
            input.Receipt = null;
            expense = await transactions.Create(input, ct);
        }
        if (expense.Currency != r.Currency)
            throw new ConflictException(ErrorCodes.CurrencyMismatch, "Receipt and expense currencies differ.")
                .WithExtension("receiptCurrency", r.Currency).WithExtension("expenseCurrency", expense.Currency);
        if (await db.Set<Receipt>().AnyAsync(other => other.ExpenseId == expense.Id, ct))
            throw new ConflictException(ErrorCodes.ReceiptAlreadyAttached, "This expense already has an itemized receipt.");
        if (request.ExpenseId != null && request.BankTransactionId is { } linkBankId)
            await bank.Link(linkBankId, new LinkBankTransactionRequest { TransactionId = expense.Id }, ct);
        // Linking only changes evidence. Existing allocations are never recalculated here.
        r.ExpenseId = expense.Id;

        foreach (var a in r.Attachments) a.ExpenseId = expense.Id;
        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return Response(r);
    }
    private static ReceiptInboxResponse Response(Receipt r) => new(r.Id, r.ExpenseId, r.Currency,
        new SaveReceiptRequest { MerchantName = r.MerchantName, PurchaseDate = r.PurchaseDate,
            Subtotal = r.Subtotal, Tax = r.Tax, Tip = r.Tip, Total = r.Total,
            Items = r.Items.OrderBy(i => i.Position).Select(i => new ReceiptItemInput { Name = i.Name,
                NormalizedName = i.NormalizedName, Description = i.Description, UnitPrice = i.UnitPrice,
                Quantity = i.Quantity, TotalPrice = i.TotalPrice, TaxAmount = i.TaxAmount,
                TaxRate = i.TaxRate, IsTaxable = i.IsTaxable }).ToList() },
        r.Attachments.Select(a => new ReceiptAttachmentResponse(a.Id, a.ExpenseId, a.BankTransactionId,
            a.ReceiptId, a.FileName, a.ContentType, a.Length, a.UploadedAt)).ToList());
}

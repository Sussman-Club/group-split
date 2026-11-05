using GroupSplit.API.Errors;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Suggestions;
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
        (await Owned().Where(r => r.ExpenseId == null && !r.IsIgnored).Include(r => r.Items).Include(r => r.Attachments)
            .OrderByDescending(r => r.PurchaseDate).ThenBy(r => r.Id).ToListAsync(ct)).Select(Response).ToList();
    public async Task<IReadOnlyList<ReceiptInboxResponse>> ListAdded(CancellationToken ct) =>
        (await Owned().Where(r => r.ExpenseId != null).Include(r => r.Items).Include(r => r.Attachments)
            .OrderByDescending(r => r.PurchaseDate).ThenBy(r => r.Id).ToListAsync(ct)).Select(Response).ToList();
    public async Task<IReadOnlyList<ReceiptInboxResponse>> ListIgnored(CancellationToken ct) =>
        (await Owned().Where(r => r.ExpenseId == null && r.IsIgnored).Include(r => r.Items).Include(r => r.Attachments)
            .OrderByDescending(r => r.PurchaseDate).ThenBy(r => r.Id).ToListAsync(ct)).Select(Response).ToList();
    public async Task<IReadOnlyList<ReceiptInboxSuggestion>> Suggest(ReceiptInboxSuggestionsRequest request, IFilingSuggester suggester, CancellationToken ct)
    {
        if (request.ReceiptIds is null || request.ReceiptIds.Count is 0 or > ReceiptInboxSuggestionsRequest.MaxRows)
            throw new ValidationException(ErrorCodes.ReceiptInvalid, "Choose between 1 and 50 receipts.");
        var ids = request.ReceiptIds.Distinct().ToArray();
        var receipts = await Owned().Where(r => ids.Contains(r.Id) && r.ExpenseId == null && !r.IsIgnored)
            .Include(r => r.Items).ToListAsync(ct);
        var rows = receipts.Where(r => r.Items.Count > 0).Select(r =>
            (new FilingInput(r.Id, r.PurchaseDate, r.Total, r.Currency, r.MerchantName ?? "Receipt", r.MerchantName),
             (FilingReceipt?)new FilingReceipt { MerchantName = r.MerchantName, Items = r.Items.OrderBy(i => i.Position).Select(i => i.Name).Take(FilingReceipt.MaxItems).ToArray() })).ToList();
        var suggestions = await suggester.Suggest(rows, ct);
        return suggestions.Select(s => new ReceiptInboxSuggestion(s.SourceId, s.Destination)).ToList();
    }
    public async Task SetIgnored(Guid id, bool ignored, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        var receipt = await Find(id, ct);
        Unfiled(receipt);
        receipt.IsIgnored = ignored;
        await db.SaveChangesAsync(ct);
    }
    public async Task<ReceiptInboxResponse> Get(Guid id, CancellationToken ct) => Response(await Find(id, ct));
    public async Task<ReceiptInboxResponse> Save(Guid id, SaveReceiptInboxRequest request, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        return await SaveCore(id, request, ct);
    }
    public async Task<ReceiptInboxResponse> SaveExtractedIfEmpty(Guid id, SaveReceiptRequest details, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        var existing = await Find(id, ct);
        Unfiled(existing);
        if (existing.Items.Count != 0 || !string.IsNullOrWhiteSpace(existing.MerchantName) ||
            existing.PurchaseDate is not null || existing.Total != 0 || existing.Subtotal != 0 ||
            existing.Tax != 0 || existing.Tip != 0)
            throw new ConflictException(ErrorCodes.ReceiptInvalid, "Receipt details have already been added. Review the receipt before changing them.");
        return await SaveCore(id, new SaveReceiptInboxRequest { Currency = existing.Currency, Receipt = details }, ct);
    }
    private async Task<ReceiptInboxResponse> SaveCore(Guid id, SaveReceiptInboxRequest request, CancellationToken ct)
    {
        var r = await Owned().Include(r => r.Items).Include(r => r.Attachments).FirstOrDefaultAsync(r => r.Id == id, ct);
        var importingDetails = r is null || r.Items.Count == 0 && string.IsNullOrWhiteSpace(r.MerchantName)
            && r.PurchaseDate is null && r.Total == 0 && r.Subtotal == 0 && r.Tax == 0 && r.Tip == 0;
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
            if (importingDetails && r.Subtotal + r.Tax + r.Tip == r.Total && r.Items.Sum(item => item.TotalPrice) == r.Subtotal)
            {
                var lines = r.Items.OrderBy(item => item.Position).ToArray();
                var taxes = ReceiptTaxes.DistributeUnassigned(r.Tax, lines.Select(item =>
                    new ReceiptTaxLine(item.TotalPrice, item.TaxAmount, item.TaxRate, item.IsTaxable)).ToArray());
                if (taxes is not null)
                    for (var index = 0; index < lines.Length; index++) lines[index].TaxAmount = taxes[index];
            }
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
            .Select(e => new ReceiptExpenseCandidate(e.Id, e.Name, e.Amount, e.Currency, e.DateTime,
                db.Set<Receipt>().Any(existing => existing.ExpenseId == e.Id) ||
                db.Set<ReceiptAttachment>().Any(existing => existing.ExpenseId == e.Id))).ToListAsync(ct);
    }
    public async Task<ReceiptInboxResponse> File(Guid id, FileReceiptInboxRequest request, CancellationToken ct)
    {
        await using var lease = await guard.Acquire(user.User.Id, ct: ct);
        var retrying = false;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (retrying) db.StartOver(user.User);
            retrying = true;
            return await FileAttempt(id, request, ct);
        });
    }
    private async Task<ReceiptInboxResponse> FileAttempt(Guid id, FileReceiptInboxRequest request, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var r = await Find(id, ct);
        if (r.ExpenseId != null)
        {
            if (request.ExpenseId != null && request.ExpenseId != r.ExpenseId)
                throw new ConflictException(ErrorCodes.ReceiptAlreadyFiled, "This receipt was filed elsewhere.");
            return Response(r);
        }
        if (r.IsIgnored) throw new ConflictException(ErrorCodes.ReceiptInvalid, "Restore this receipt before filing it.");
        if (new[] { request.ExpenseId != null, request.BankTransactionId != null && request.ExpenseId == null, request.NewExpense != null }.Count(x => x) != 1)
            throw new ValidationException(ErrorCodes.ReceiptInvalid, "Choose one filing destination.");
        // A new expense may carry a reviewed allocation of previously unassigned tax.
        // Validate that draft below before applying its line taxes to the inbox receipt.
        if (r.Items.Count > 0 && (request.NewExpense is null || request.NewExpense.Receipt is null))
        { ReceiptSplitCalculator.CalculateLineTaxes(r); ReceiptSplitCalculator.RefuseIfFiguresDisagree(r); }
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
            if (input.Receipt is { } submitted && r.Items.Count > 0)
            {
                var saved = Response(r).Receipt;
                if (submitted.Items.Count != saved.Items.Count)
                    throw new ValidationException(ErrorCodes.ReceiptInvalid, "The receipt items changed. Review the receipt and try again.");
                input.Receipt = saved with { Items = saved.Items.Select((item, index) => item with
                    { SplitRuleVersionId = submitted.Items[index].SplitRuleVersionId,
                        TaxAmount = submitted.Items[index].TaxAmount }).ToArray() };
            }
            else input.Receipt = null;
            expense = await transactions.Create(input, ct);
            if (expense.Receipt is { } prepared)
            {
                var sourceItems = r.Items.OrderBy(item => item.Position).ToArray();
                var preparedItems = prepared.Items.OrderBy(item => item.Position).ToArray();
                for (var index = 0; index < sourceItems.Length; index++)
                {
                    sourceItems[index].SplitRuleVersionId = preparedItems[index].SplitRuleVersionId;
                    sourceItems[index].TaxAmount = preparedItems[index].TaxAmount;
                }
                ReceiptSplitCalculator.RefuseIfFiguresDisagree(r);
                db.Remove(prepared);
                expense.Receipt = null;
                await db.SaveChangesAsync(ct);
            }
        }
        if (expense.Currency != r.Currency)
            throw new ConflictException(ErrorCodes.CurrencyMismatch, "Receipt and expense currencies differ.")
                .WithExtension("receiptCurrency", r.Currency).WithExtension("expenseCurrency", expense.Currency);
        if (await db.Set<Receipt>().AnyAsync(other => other.ExpenseId == expense.Id, ct))
            throw new ConflictException(ErrorCodes.ReceiptAlreadyAttached, "This expense already has an itemized receipt.");
        if (request.ExpenseId is not null && await db.Set<ReceiptAttachment>().AnyAsync(existing => existing.ExpenseId == expense.Id, ct))
            throw new ConflictException(ErrorCodes.ReceiptAlreadyAttached, "This expense already has a receipt file.");
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

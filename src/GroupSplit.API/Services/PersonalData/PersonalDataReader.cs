using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.PersonalData;

/// <summary>Applies existing API visibility rules and projects only portable, credential-free records.</summary>
public sealed class PersonalDataReader(AppDbContext db, ICurrentUser currentUser,
    IGroupService groups, TimeProvider clock)
{
    public async Task<ExportRecords> Read(CancellationToken ct)
    {
        var user = currentUser.User;
        var visibleGroups = await groups.GetAllGroups(ct);
        var activity = await groups.GetUserActivity(ct);
        var visibleExpenses = db.Set<Expense>().Where(expense => expense.UserId == user.Id ||
            visibleGroups.Any(group => group.Id == expense.GroupId));
        var ownRows = db.Set<BankTransaction>().Where(row => row.Account.Connection.UserId == user.Id);
        var requests = db.Set<SettlementRequest>()
            .Where(row => row.RequesterUserId == user.Id || row.RecipientUserId == user.Id);

        // Materialise only the file inventory: the writer needs it after streaming JSON.
        var attachments = await db.Set<ReceiptAttachment>().Where(file =>
                file.ExpenseId != null && visibleExpenses.Any(expense => expense.Id == file.ExpenseId) ||
                file.ExpenseId == null && file.BankTransactionId != null && ownRows.Any(row => row.Id == file.BankTransactionId))
            .OrderBy(file => file.Id).Select(file => new ExportAttachment(file.Id, file.ExpenseId,
                file.ExpenseId == null ? file.BankTransactionId : null, file.FileName,
                file.ContentType, file.Length, file.UploadedAt)).ToListAsync(ct);

        return new ExportRecords(
            clock.GetUtcNow(),
            new ExportProfile(user.Id, user.FirstName, user.LastName, user.Email),
            visibleGroups.OrderBy(group => group.Id).Select(group =>
                new ExportGroup(group.Id, group.Name, group.Description, group.Currency)).AsAsyncEnumerable(),
            db.Set<GroupMembership>().IgnoreQueryFilters().Where(row => row.UserId == user.Id)
                .OrderBy(row => row.GroupId).Select(row => new ExportMembership(row.GroupId, row.JoinedAt,
                    row.ArchivedAt, row.LeftAt, row.LeftReason)).AsAsyncEnumerable(),
            activity.OrderBy(row => row.Id).Select(row => new ExportTransaction(
                row.Id, row is Expense ? "expense" : "transfer", row.GroupId, row.UserId,
                row.Name, row.Description, row.Amount, row.Currency, row.DateTime,
                row is Expense ? ((Expense)row).CategoryId : null,
                row.Group.Name, row is Expense && ((Expense)row).Category != null ? ((Expense)row).Category!.Name : null,
                row.Splits.OrderBy(split => split.UserId).Select(split => new ExportShare(split.UserId, split.Amount)).ToList()
            )).AsAsyncEnumerable(),
            db.Set<User>().Where(person => person.Id == user.Id || activity.Any(row => row.UserId == person.Id ||
                    row.Splits.Any(split => split.UserId == person.Id)) || requests.Any(request =>
                        request.RequesterUserId == person.Id || request.RecipientUserId == person.Id)).OrderBy(person => person.Id)
                .Select(person => new ExportPerson(person.Id, person.FirstName, person.LastName, person.Kind)).AsAsyncEnumerable(),
            db.Set<Category>().Where(row => visibleGroups.Any(group => group.Id == row.Group.Id)).OrderBy(row => row.Id)
                .Select(row => new ExportCategory(row.Id, row.Group.Id, row.Name, row.Description, row.ArchivedAt)).AsAsyncEnumerable(),
            db.Set<Receipt>().Where(row => visibleExpenses.Any(expense => expense.Id == row.ExpenseId)).OrderBy(row => row.Id)
                .Select(row => new ExportReceipt(row.Id, row.ExpenseId, row.Subtotal, row.Tax, row.Tip, row.Total,
                    row.MerchantName, row.PurchaseDate, row.Items.OrderBy(item => item.Position).ThenBy(item => item.Id)
                        .Select(item => new ExportReceiptItem(item.Id, item.Position, item.Name, item.Description,
                            item.UnitPrice, item.Quantity, item.TotalPrice, item.TaxAmount, item.TaxRate, item.IsTaxable)).ToList())).AsAsyncEnumerable(),
            db.Set<BankConnection>().Where(row => row.UserId == user.Id).OrderBy(row => row.Id)
                .Select(row => new ExportBankConnection(row.Id, row.Provider, row.InstitutionName,
                    row.Status, row.LinkedAt, row.LastSyncedAt)).AsAsyncEnumerable(),
            db.Set<LinkedAccount>().Where(row => row.Connection.UserId == user.Id).OrderBy(row => row.Id)
                .Select(row => new ExportBankAccount(row.Id, row.BankConnectionId, row.Name, row.Mask,
                    row.Type, row.Subtype, row.Currency)).AsAsyncEnumerable(),
            ownRows.OrderBy(row => row.Id).Select(row => new ExportBankTransaction(row.Id, row.LinkedAccountId,
                row.Date, row.Amount, row.Currency, row.Description, row.MerchantName, row.Pending, row.Status,
                row.ImportedAt, row.FiledAs != null ? row.FiledAs.Id : null, row.ReplacesId)).AsAsyncEnumerable(),
            requests
                .OrderBy(row => row.Id).Select(row => new ExportSettlementRequest(row.Id, row.RequesterUserId,
                    row.RecipientUserId, row.Currency, row.Amount, row.CreatedAt, row.Through,
                    row.CancelledAt, row.ResolvedAt, row.StaleAt)).AsAsyncEnumerable(),
            attachments);
    }
}

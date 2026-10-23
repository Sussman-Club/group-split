using GroupSplit.Data.Entities;

namespace GroupSplit.API.Services.PersonalData;

// This is the public archive contract, independent of EF entities and API response DTOs.
// Only explicit allowlisted fields belong here. Additions require a privacy review.
public sealed record ExportRecords(
    DateTimeOffset ExportedAt,
    ExportProfile Profile,
    IAsyncEnumerable<ExportGroup> Groups,
    IAsyncEnumerable<ExportMembership> Memberships,
    IAsyncEnumerable<ExportTransaction> Transactions,
    IAsyncEnumerable<ExportPerson> People,
    IAsyncEnumerable<ExportCategory> Categories,
    IAsyncEnumerable<ExportReceipt> Receipts,
    IAsyncEnumerable<ExportBankConnection> BankConnections,
    IAsyncEnumerable<ExportBankAccount> BankAccounts,
    IAsyncEnumerable<ExportBankTransaction> BankTransactions,
    IAsyncEnumerable<ExportSettlementRequest> SettlementRequests,
    IReadOnlyList<ExportAttachment> Attachments)
{
    public int SchemaVersion => 1;
    public string Format => "groupsplit-personal-data";
}

public sealed record ExportProfile(Guid Id, string? FirstName, string? LastName, string? Email);
public sealed record ExportGroup(Guid Id, string Name, string Description, string Currency);
public sealed record ExportMembership(Guid GroupId, DateTimeOffset JoinedAt,
    DateTimeOffset? ArchivedAt, DateTimeOffset? LeftAt, LeftReason? LeftReason);
public sealed record ExportTransaction(Guid Id, string Kind, Guid GroupId, Guid UserId,
    string Name, string? Description, decimal Amount, string Currency, DateTimeOffset DateTime,
    Guid? CategoryId, string GroupName, string? CategoryName, IReadOnlyList<ExportShare> Splits);
public sealed record ExportShare(Guid UserId, decimal Amount);
public sealed record ExportPerson(Guid Id, string? FirstName, string? LastName, UserKind Kind);
public sealed record ExportCategory(Guid Id, Guid GroupId, string Name, string Description, DateTimeOffset? ArchivedAt);
public sealed record ExportReceipt(Guid Id, Guid ExpenseId, decimal Subtotal, decimal Tax,
    decimal Tip, decimal Total, string? MerchantName, DateOnly? PurchaseDate, IReadOnlyList<ExportReceiptItem> Items);
public sealed record ExportReceiptItem(Guid Id, int Position, string Name, string? Description,
    decimal UnitPrice, decimal Quantity, decimal TotalPrice, decimal TaxAmount, decimal? TaxRate, bool? IsTaxable);
public sealed record ExportBankConnection(Guid Id, string Provider, string InstitutionName,
    BankConnectionStatus Status, DateTimeOffset LinkedAt, DateTimeOffset? LastSyncedAt);
public sealed record ExportBankAccount(Guid Id, Guid BankConnectionId, string Name, string? Mask,
    string Type, string? Subtype, string Currency);
public sealed record ExportBankTransaction(Guid Id, Guid LinkedAccountId, DateOnly Date,
    decimal Amount, string Currency, string Description, string? MerchantName, bool Pending,
    BankTransactionStatus Status, DateTimeOffset ImportedAt, Guid? FiledAsId, Guid? ReplacesId);
public sealed record ExportSettlementRequest(Guid Id, Guid RequesterUserId, Guid RecipientUserId,
    string Currency, decimal Amount, DateTimeOffset CreatedAt, DateOnly? Through,
    DateTimeOffset? CancelledAt, DateTimeOffset? ResolvedAt, DateTimeOffset? StaleAt);
public sealed record ExportAttachment(Guid Id, Guid? ExpenseId, Guid? BankTransactionId,
    string FileName, string ContentType, long Length, DateTimeOffset UploadedAt)
{
    // Neither original filenames nor storage keys can control paths inside the ZIP.
    public string Path => $"receipts/{Id}" + (ContentType switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp",
        "application/pdf" => ".pdf", _ => ".bin"
    });
}

namespace GroupSplit.Data.Entities;

/// <summary>A source image or document attached to a pending bank row or expense.</summary>
public class ReceiptAttachment : Entity
{
    public Guid? ExpenseId { get; set; }
    public virtual Expense? Expense { get; set; }
    public Guid? BankTransactionId { get; set; }
    public virtual BankTransaction? BankTransaction { get; set; }
    public Guid? ReceiptId { get; set; }
    public virtual Receipt? Receipt { get; set; }
    public required string ObjectKey { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Length { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

namespace GroupSplit.Data.Entities;

/// <summary>A receipt owner's decision that a receipt and expense are not the same purchase.</summary>
public class ReceiptMatchDismissal : Entity
{
    public Guid ReceiptId { get; set; }
    public virtual Receipt Receipt { get; set; } = null!;
    public Guid ExpenseId { get; set; }
    public virtual Expense Expense { get; set; } = null!;
}

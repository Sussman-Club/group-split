namespace GroupSplit.Data.Entities;

/// <summary>The itemized bill for one expense.</summary>
public class Receipt : Entity
{
    public virtual Expense Expense { get; set; } = null!;
    public Guid ExpenseId { get; set; }
    public required decimal Subtotal { get; set; }

    /// <summary>
    /// What the bill charged in tax, which is the lines' own tax added up -- tax is carried
    /// per line so that one bill can charge two rates.
    /// </summary>
    public decimal Tax { get; set; }

    /// <summary>
    /// Belongs to no line, and is spread over them by price when the bill is divided.
    /// </summary>
    public decimal Tip { get; set; }

    /// <summary>Subtotal, tax and tip -- and the expense's amount, or it will not divide.</summary>
    public required decimal Total { get; set; }
    public virtual ICollection<ReceiptItem> Items { get; } = [];
}

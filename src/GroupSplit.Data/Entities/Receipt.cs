namespace GroupSplit.Data.Entities;

/// <summary>The itemized bill for one expense.</summary>
public class Receipt : Entity
{
    public virtual Expense Expense { get; set; } = null!;
    public Guid ExpenseId { get; set; }
    public required decimal Subtotal { get; set; }

    /// <summary>The vendor wording read from the source document. It is not forced through
    /// merchant matching: a receipt is evidence, so an uncertain reading remains visible.</summary>
    public string? MerchantName { get; set; }

    /// <summary>The calendar day printed on the receipt, when the provider can read one.</summary>
    public DateOnly? PurchaseDate { get; set; }

    /// <summary>Provider-returned text/values retained for a person reviewing an OCR result.</summary>
    public string? SourceValues { get; set; }

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
    public virtual ICollection<ReceiptAdjustment> Adjustments { get; } = [];

    /// <summary>Source photos and documents used to transcribe this bill.</summary>
    public virtual ICollection<ReceiptAttachment> Attachments { get; } = [];
}

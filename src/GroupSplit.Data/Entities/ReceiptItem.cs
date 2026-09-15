namespace GroupSplit.Data.Entities;

/// <summary>A line on an expense's bill, divided by a saved rule version.</summary>
public class ReceiptItem : Entity
{
    public virtual Receipt Receipt { get; set; } = null!;
    public Guid ReceiptId { get; set; }
    /// <summary>
    /// Where the line sits on the paper. Stored rather than left to the database, because a
    /// bill is read against the receipt in somebody's hand -- and because the division breaks
    /// its rounding ties on a stable order, which an unordered read would not give it.
    /// </summary>
    public int Position { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    /// <summary>The original extracted wording, retained separately from the display name.</summary>
    public string? Description { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; } = 1;
    // Kept as printed: discounts and till rounding need not equal quantity * unit price.
    public required decimal TotalPrice { get; set; }
    /// <summary>
    /// The tax charged on this line, which is zero unless the bill said otherwise: a
    /// restaurant bill under VAT charges none on top of its prices.
    /// </summary>
    public decimal TaxAmount { get; set; }
    /// <summary>Null while editing; required before division. Never follows later rule edits.</summary>
    public Guid? SplitRuleVersionId { get; set; }
    public virtual SplitRuleVersion? SplitRuleVersion { get; set; }
}

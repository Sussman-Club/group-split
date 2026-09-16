using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// The bill, whole. A line the request does not carry is a line that has gone, so correcting
/// one price means sending the others back with the ids they were saved under.
/// </summary>
public record SaveReceiptRequest
{
    [MaxDecimalPlaces(2)] public decimal Subtotal { get; init; }
    [MaxDecimalPlaces(2)] public decimal Tax { get; init; }
    [MaxDecimalPlaces(2)] public decimal Tip { get; init; }
    [MaxDecimalPlaces(2)] public decimal Total { get; init; }
    [MinLength(1)] public IReadOnlyList<ReceiptItemInput> Items { get; init; } = [];
}

public record ReceiptItemInput
{
    /// <summary>
    /// Which stored line this is, for a bill being corrected. Null is a new line, which is
    /// every line of a bill being written down for the first time.
    /// </summary>
    public Guid? Id { get; init; }
    [Required, StringLength(128)] public string Name { get; init; } = null!;
    [StringLength(128)] public string? NormalizedName { get; init; }
    /// <summary>
    /// The source wording. On an existing line, null leaves the saved wording unchanged;
    /// an empty string explicitly clears it.
    /// </summary>
    [StringLength(1000)] public string? Description { get; init; }
    [MaxDecimalPlaces(2)] public decimal UnitPrice { get; init; }
    [MaxDecimalPlaces(3)] public decimal Quantity { get; init; } = 1;
    [MaxDecimalPlaces(2)] public decimal TotalPrice { get; init; }
    [MaxDecimalPlaces(2)] public decimal TaxAmount { get; init; }
    /// <summary>The exact saved version to use. Null leaves this line unfinished.</summary>
    public Guid? SplitRuleVersionId { get; init; }
}

/// <summary>
/// The mutable line model a JSON Patch is applied to when correcting one saved item.
/// It starts as the stored line, so an operation only needs to carry the fields that changed.
/// </summary>
public sealed class ReceiptItemPatch
{
    [Required, StringLength(128)] public string Name { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }
    [MaxDecimalPlaces(2)] public decimal UnitPrice { get; set; }
    [MaxDecimalPlaces(3)] public decimal Quantity { get; set; } = 1;
    [MaxDecimalPlaces(2)] public decimal TotalPrice { get; set; }
    [MaxDecimalPlaces(2)] public decimal TaxAmount { get; set; }
    public Guid? SplitRuleVersionId { get; set; }
}

/// <summary>One line's rule. No version clears it, which leaves the bill undividable.</summary>
public record SetReceiptItemRuleRequest
{
    public Guid? SplitRuleVersionId { get; init; }
}

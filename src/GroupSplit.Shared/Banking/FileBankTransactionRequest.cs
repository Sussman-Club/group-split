using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// Turns an imported row into an expense.
/// </summary>
/// <remarks>
/// Everything the row already knows -- the date, the amount, the currency -- is taken from
/// the row and cannot be sent here: filing copies what the bank said, and correcting the
/// bank is editing the expense afterwards. What the person adds is where it belongs and
/// what it was for.
/// </remarks>
public record FileBankTransactionRequest
{
    /// <summary>The group to file it in, or null to keep it personal.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>What it was for. A category naming a rule divides the expense by it.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// Who paid, when it was not the person filing. Only meaningful in a group.
    /// </summary>
    public Guid? PaidByUserId { get; set; }

    /// <summary>
    /// Exactly how to divide it, or null to divide it the way the category says.
    /// </summary>
    public IReadOnlyList<SplitInput>? Splits { get; set; }

    /// <summary>
    /// What to call the expense. Defaults to the merchant the bank named.
    /// </summary>
    [StringLength(124, ErrorMessage = "Name must be less than 124 characters.")]
    public string? Name { get; set; }

    [StringLength(256, ErrorMessage = "Description must be less than 256 characters.")]
    public string? Description { get; set; }
}

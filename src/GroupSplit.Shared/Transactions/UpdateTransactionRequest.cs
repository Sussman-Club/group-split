using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record UpdateTransactionRequest
{
    [Required] public Guid PaidByUserId { get; set; }

    /// <summary>
    /// What it is filed under, or null for none. Optional, unlike the rule version it
    /// replaces: an expense under no category divides evenly.
    /// </summary>
    public Guid? CategoryId { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(124, ErrorMessage = "Name must be less than 124 characters.")]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessage = "Description must be less than 256 characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Amount must be a number with no more than 2 decimal places.")]
    public decimal Amount { get; set; }

    [Required(ErrorMessage = "Date is required.")]
    public DateTimeOffset DateTime { get; set; }
};
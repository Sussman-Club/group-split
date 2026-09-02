using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record UpdateTransactionRequest
{
    [Required] public Guid PaidByUserId { get; set; }
    [Required] public Guid RuleVersionId { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(124, ErrorMessage = "Name must be less than 124 characters.")]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessage = "Description must be less than 256 characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    public decimal Amount { get; set; }

    [Required(ErrorMessage = "Date is required.")]
    public DateTimeOffset DateTime { get; set; }
};
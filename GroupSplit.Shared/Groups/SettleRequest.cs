using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record SettleRequest
{
    public Guid UserId { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    [DecimalScale(2, ErrorMessage = "Amount must be a number with no more than 2 decimal places.")]
    [GreaterThan(0, ErrorMessage = "Amount must be greater than or equal to 0.")]
    public decimal Amount { get; set; }
}
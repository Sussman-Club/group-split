using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record SettleRequest
{
    public Guid UserId { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    public decimal Amount { get; set; }
}
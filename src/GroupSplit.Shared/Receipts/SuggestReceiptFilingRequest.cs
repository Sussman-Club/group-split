using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public sealed record SuggestReceiptFilingRequest
{
    [Required] public SaveReceiptRequest Receipt { get; init; } = null!;
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; init; } = "USD";
    public Guid? GroupId { get; init; }
}

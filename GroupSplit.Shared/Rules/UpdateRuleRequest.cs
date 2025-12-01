using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record UpdateRuleRequest
{
    [Required(ErrorMessage = "Category is required")]
    [StringLength(64, ErrorMessage = "Category must be less than 64 characters.")]
    public string Category { get; set; } = null!;

    public RuleVersionDto Version { get; set; } = null!;
}
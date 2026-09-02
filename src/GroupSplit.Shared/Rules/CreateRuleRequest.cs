using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record CreateRuleRequest
{
    public Guid GroupId { get; init; }

    [Required(ErrorMessage = "Category is required")]
    [StringLength(64, ErrorMessage = "Category must be less than 64 characters.")]
    public string Category { get; init; } = null!;

    public RuleVersionDto Version { get; init; } = null!;
};
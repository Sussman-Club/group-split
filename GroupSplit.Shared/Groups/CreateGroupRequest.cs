using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record CreateGroupRequest
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(64, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 64 characters.")]
    public string Name { get; init; } = null!;
}
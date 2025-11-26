using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record CreateGroupRequest
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; set; } = null!;
}
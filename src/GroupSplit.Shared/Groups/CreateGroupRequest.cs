using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record CreateGroupRequest
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; set; } = null!;

    /// <summary>The fallback rule for uncategorized expenses.</summary>
    /// <remarks>New groups always start with Even; this is changed through the group PATCH.</remarks>
    public Guid? DefaultSplitRuleId { get; set; }
}

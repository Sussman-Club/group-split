using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record CreateGroupRequest
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessage = "Description must be 256 characters or fewer.")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The fallback rule for uncategorized expenses.</summary>
    /// <remarks>New groups always start with Even; this is changed through the group PATCH.</remarks>
    public Guid? DefaultSplitRuleId { get; set; }

    /// <summary>The span the group's Activity tab opens on, for every member.</summary>
    [EnumDataType(typeof(ActivityPeriod), ErrorMessage = "Default period is not one of the periods offered.")]
    public ActivityPeriod DefaultPeriod { get; set; } = ActivityPeriod.ThisMonth;
}

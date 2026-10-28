using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record CreateGroupRequest
{
    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameRequired))]
    [StringLength(64, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameLength64))]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionLength))]
    public string Description { get; set; } = string.Empty;

    /// <summary>The fallback rule for uncategorized expenses.</summary>
    /// <remarks>New groups always start with Even; this is changed through the group PATCH.</remarks>
    public Guid? DefaultSplitRuleId { get; set; }

    /// <summary>The span the group's Activity tab opens on, for every member.</summary>
    [EnumDataType(typeof(ActivityPeriod), ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DefaultPeriod))]
    public ActivityPeriod DefaultPeriod { get; set; } = ActivityPeriod.ThisMonth;
}

using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// What an expense was for, and how the group usually divides that kind of expense.
/// </summary>
/// <param name="DefaultSplitRuleId">
/// The rule an expense in this category is pre-filled from, or null to divide it evenly
/// between the group's members. A category without one is ordinary, not broken -- which is
/// the difference from the rule it replaces, where a category with no rule could record
/// nothing at all.
/// </param>
/// <param name="IsArchive">
/// True once the group has retired this category. It is left out of the listing unless it is
/// asked for, so a client holding one of these is either showing what a group files under
/// today or was explicitly asked for the whole set -- and every expense already filed under
/// an archived category still names it.
/// </param>
public record CategoryResponse(
    Guid Id,
    Guid GroupId,
    string Name,
    Guid? DefaultSplitRuleId,
    string? DefaultSplitRuleName,
    bool IsArchive = false)
{
    public string Description { get; init; } = string.Empty;
}

public record CreateCategoryRequest
{
    public Guid GroupId { get; init; }

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameRequiredShort))]
    [StringLength(64, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameLength64))]
    public string Name { get; init; } = null!;

    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionLength))]
    public string Description { get; init; } = string.Empty;

    public Guid? DefaultSplitRuleId { get; init; }
}

public record UpdateCategoryRequest
{
    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameRequiredShort))]
    [StringLength(64, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameLength64))]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionLength))]
    public string Description { get; set; } = string.Empty;

    public Guid? DefaultSplitRuleId { get; set; }
}

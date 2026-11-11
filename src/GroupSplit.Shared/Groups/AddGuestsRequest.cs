using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// People to add to a group as guests, by name.
/// </summary>
/// <remarks>
/// A list, because adding the three people somebody went on the trip with is one thing and
/// not three.
/// </remarks>
public record AddGuestsRequest
{
    /// <summary>
    /// Who to add. Each one is bounded here rather than only in the database, so an over-long
    /// name is a refusal naming the field rather than a 500.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.GuestRequired))]
    [MaxItemLength(InviteToGroupRequest.NameLength, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.GuestNameLength))]
    public List<string> Names { get; set; } = [];

    /// <summary>
    /// Whether to issue each of them a link straight away. Off by default: most guests never
    /// sign up, and a link can be sent at any time later.
    /// </summary>
    public bool IssueLinks { get; set; }
}

/// <summary>How to issue a guest's link.</summary>
public record IssueGuestLinkRequest
{
    /// <summary>
    /// Replace the link if there is one, so the old one stops working. Without it, asking
    /// again returns the link already out, so "send link" can always just copy -- unless
    /// that link has expired, when a new one is issued in its place either way.
    /// </summary>
    public bool Replace { get; set; }
}

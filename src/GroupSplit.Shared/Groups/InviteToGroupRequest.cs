using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// People to ask into a group, by name.
/// </summary>
/// <remarks>
/// Names, where this used to be email addresses. An address was the handle, the display name
/// and the way an invitee found their own invitations all at once, and it meant a group could
/// only ask somebody whose address they had -- which is not how most people know the friend
/// they went on the trip with. Inviting now makes a person and a link, and the group sends
/// the link through whatever they actually talk on.
/// <para>
/// A list, because inviting three people at once is one thing somebody does and not three.
/// </para>
/// </remarks>
public record InviteToGroupRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "Name at least one person to invite.")]
    public List<string> Names { get; set; } = [];
}

using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

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
    /// <summary>
    /// Who to ask, by name. Each one is bounded here rather than only in the database: the
    /// name is written to two 64-character columns -- the invitation's, and the stand-in
    /// account's first name -- and an over-long one used to reach them and come back as a
    /// 500 rather than as a refusal naming the field.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "Name at least one person to invite.")]
    [MaxItemLength(NameLength, ErrorMessage = "A name can be at most 64 characters.")]
    public List<string> Names { get; set; } = [];

    /// <summary>
    /// What the columns behind a name hold. Public so a client can stop somebody typing
    /// past it rather than letting them find out on Save.
    /// </summary>
    public const int NameLength = 64;
}

using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Every write to a group, in one place.
/// </summary>
/// <remarks>
/// There used to be two ways to write. Pages went through their page state service, which
/// announced the change so every other page re-read what it held; dialogs called the
/// generated client directly and announced themselves, or -- for rules -- did not, which is
/// why editing a rule left the group page showing the old one until it was reloaded.
/// <para>
/// A command is the only way now, and it does the three things a write has to do: run the
/// call through <see cref="Errors.ApiErrorPresenter"/> so a refusal is shown once and the
/// caller is handed a false rather than an exception, say what happened to what, and
/// announce it so the page states catch up. A page state service is left holding what it
/// reads, which is the only thing it was ever good at.
/// </para>
/// <para>
/// Each returns whether it completed, so a dialog can close on success and stay open on a
/// refusal without catching anything.
/// </para>
/// </remarks>
public interface IGroupCommands
{
    Task<GroupResponse?> CreateAsync(CreateGroupRequest request, CancellationToken ct = default);

    Task<bool> RenameAsync(Guid groupId, JsonPatchDocument<CreateGroupRequest> patch,
        CancellationToken ct = default);

    /// <summary>Asks people to join, by email. Nobody is added without accepting.</summary>
    Task<bool> InviteAsync(Guid groupId, string groupName, AddMemberRequest request,
        CancellationToken ct = default);

    Task<bool> WithdrawInvitationAsync(Guid groupId, Guid invitationId, string email,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a shareable link into the group, putting out whatever link it had. Also how
    /// one is reset, since there is nothing to say about the old one once it is replaced.
    /// </summary>
    Task<GroupJoinLinkResponse?> CreateJoinLinkAsync(Guid groupId, string groupName,
        CancellationToken ct = default);

    /// <summary>Puts out the group's links, so the URLs already shared stop working.</summary>
    Task<bool> RevokeJoinLinkAsync(Guid groupId, string groupName, CancellationToken ct = default);

    /// <summary>
    /// Follows a link somebody was sent. Answers with the group either way, saying whether
    /// this is what put them in it.
    /// </summary>
    Task<JoinedGroupResponse?> JoinByLinkAsync(string token, CancellationToken ct = default);

    Task<bool> RemoveMemberAsync(Guid groupId, Guid memberUserId, string memberName,
        CancellationToken ct = default);

    /// <summary>The caller taking themselves out, which needs a settled balance.</summary>
    Task<bool> LeaveAsync(Guid groupId, string groupName, CancellationToken ct = default);

    Task<bool> SetArchivedAsync(Guid groupId, string groupName, bool archived,
        CancellationToken ct = default);

    /// <summary>
    /// Records a repayment. <paramref name="request"/> says which way the money went, so
    /// either side can record one.
    /// </summary>
    Task<bool> SettleAsync(Guid groupId, SettleRequest request, string otherName,
        CancellationToken ct = default);

    /// <summary>
    /// Records every repayment between the caller and the rest of a group at once. Null when
    /// it did not go through, so the caller can leave its dialog open.
    /// </summary>
    Task<SettleUpResponse?> SettleUpAsync(Guid groupId, SettleUpRequest request,
        CancellationToken ct = default);

    Task<GroupResponse?> AcceptInvitationAsync(Guid invitationId, string groupName,
        CancellationToken ct = default);

    Task<bool> DeclineInvitationAsync(Guid invitationId, string groupName,
        CancellationToken ct = default);
}

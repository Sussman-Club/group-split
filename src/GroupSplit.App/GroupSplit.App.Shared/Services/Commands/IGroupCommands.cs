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

    Task<GroupResponse?> AcceptInvitationAsync(Guid invitationId, string groupName,
        CancellationToken ct = default);

    Task<bool> DeclineInvitationAsync(Guid invitationId, string groupName,
        CancellationToken ct = default);
}

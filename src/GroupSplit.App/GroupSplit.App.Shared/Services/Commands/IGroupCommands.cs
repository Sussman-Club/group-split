using GroupSplit.Shared;

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

    Task<bool> RenameAsync(Guid groupId, PatchDocument<CreateGroupRequest> patch,
        CancellationToken ct = default);

    /// <summary>
    /// Adds people the group is sharing costs with as guests, by name. Nobody is sent
    /// anything: a guest has no link until a member issues one with
    /// <see cref="IssueGuestLinkAsync"/>.
    /// </summary>
    Task<bool> AddGuestsAsync(Guid groupId, string groupName, AddGuestsRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// The guest with a link to take their place. Without <paramref name="replace"/> it is
    /// the link already out, if there is one -- so "send link" and "copy link" are the same
    /// call -- and with it a new one, which stops the old one working. Null when it did not
    /// go through.
    /// </summary>
    /// <remarks>
    /// Says nothing on success. Issuing a link is always the first half of copying it, and
    /// the component that copies is the one that knows whether the clipboard took it, so
    /// the sentence afterwards is its to say.
    /// </remarks>
    Task<GroupGuestResponse?> IssueGuestLinkAsync(Guid groupId, Guid guestUserId, bool replace,
        CancellationToken ct = default);

    /// <summary>
    /// Stops a guest's link working. The guest stays, with everything recorded for them; a
    /// new link can be sent whenever somebody likes.
    /// </summary>
    Task<bool> RevokeGuestLinkAsync(Guid groupId, Guid guestUserId, string name,
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

    /// <summary>
    /// Takes a member or a guest out of the group. Either has to be square first; a refusal
    /// for that says who owes what, from the balance the API sends back with it.
    /// </summary>
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
    /// Records a repayment between two people, neither of whom has to be the caller. How a
    /// member writes down what two guests squared up between themselves: neither of them can
    /// sign in to say so. The names are only for the message that follows.
    /// </summary>
    Task<bool> RecordRepaymentAsync(Guid groupId, RecordRepaymentRequest request, string fromName,
        string toName, CancellationToken ct = default);

    /// <summary>
    /// Records every repayment between the caller and the rest of a group at once. Null when
    /// it did not go through, so the caller can leave its dialog open.
    /// </summary>
    Task<SettleUpResponse?> SettleUpAsync(Guid groupId, SettleUpRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Claims a guest's link: joins the group in the guest's place, taking on whatever the
    /// group had recorded for them.
    /// </summary>
    Task<InvitationClaimedResponse?> ClaimInvitationAsync(string token,
        CancellationToken ct = default);

    /// <summary>
    /// Says a guest's link is not for whoever opened it. Only the link stops: the guest
    /// stays in the group exactly as they were, and nothing recorded for them moves.
    /// </summary>
    Task<bool> DeclineInvitationAsync(string token, string groupName,
        CancellationToken ct = default);
}

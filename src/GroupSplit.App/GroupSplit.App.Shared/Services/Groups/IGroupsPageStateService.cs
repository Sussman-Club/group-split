using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Groups;

/// <summary>
/// The state behind the groups page. The operations return whether they completed: a
/// failure has already been shown to the person by the time they return false, so a caller
/// only needs the answer to decide whether to move on.
/// </summary>
public interface IGroupsPageStateService
{
    ICollection<GroupResponse> Groups { get; }

    GroupResponse? SelectedGroup { get; set; }

    /// <summary>
    /// The newest expenses in the selected group -- one page of them, which is what the
    /// card on the page shows -- and, on the page itself, how many there are in all.
    /// </summary>
    PagedResponse<TransactionResponse>? Transactions { get; }

    UserGroupBalanceResponse? Balance { get; }

    /// <summary>
    /// Where this person stands across every group at once: what they are owed, what they
    /// owe, and the balance in each group.
    /// </summary>
    /// <remarks>
    /// Read here rather than per card, because it is one call that answers for every group
    /// -- and because the question it answers is not about the selected group at all. The
    /// home page leads with it.
    /// </remarks>
    UserPositionResponse? Position { get; }

    /// <summary>True while the selected group's expenses and balances are on their way.</summary>
    bool IsLoading { get; }

    /// <summary>
    /// Makes sure <see cref="Transactions"/> and <see cref="Balance"/> are the selected
    /// group's own, reading them if they are not, and completes when they have landed.
    /// </summary>
    /// <remarks>
    /// A page opening on a group calls this rather than trusting what is held: the
    /// selection and its figures are persisted across the prerender separately, and the
    /// figures beside a restored selection can be another group's, or nobody's yet.
    /// </remarks>
    Task EnsureSelectedLoadedAsync();

    event Action? OnGroupSelected;
    event Action? OnTransactionsChanged;
    event Action? OnGroupsChanged;

    Task IsReadyTask { get; }

    Task<bool> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// People who are no longer in the selected group but remain in its history: members who
    /// left or were removed, and guests who were removed -- told apart by
    /// <see cref="UserInfo.IsGuest"/>.
    /// </summary>
    Task<IReadOnlyList<UserInfo>> GetGroupPastMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The selected group's guests, with who added them and whether a link is out for each.
    /// </summary>
    /// <remarks>
    /// The members listing carries the same people, marked as guests, because every screen
    /// that chooses a person needs them. This is the People tab's wider answer about them:
    /// the link's token included, which only the group's members may read.
    /// </remarks>
    Task<IReadOnlyList<GroupGuestResponse>> GetGroupGuestsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds people to a group as guests, by name. Nobody is sent anything.
    /// </summary>
    /// <param name="groupId">
    /// The group the dialog was opened from. Every write that follows a dialog names its
    /// group, captured when the dialog opened: the selection at confirm time can be another
    /// group by then.
    /// </param>
    Task<bool> AddGuestsAsync(Guid groupId, AddGuestsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A link to take a guest's place: the one already out, or with
    /// <paramref name="replace"/> a new one that stops the old one working. Null when it
    /// did not go through.
    /// </summary>
    Task<GroupGuestResponse?> IssueGuestLinkAsync(Guid groupId, Guid guestUserId, bool replace,
        CancellationToken cancellationToken = default);

    /// <summary>Stops a guest's link working. The guest stays.</summary>
    Task<bool> RevokeGuestLinkAsync(Guid groupId, Guid guestUserId, string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The selected group's standing join link, or null when nobody has made one. A group
    /// has one at a time; the API answers with a list because a race could leave two, and
    /// this takes the newest, which is the one the members page offers to share.
    /// </summary>
    Task<GroupJoinLinkResponse?> GetGroupJoinLinkAsync(CancellationToken cancellationToken = default);

    /// <summary>Issues a link into the selected group, replacing whatever it had.</summary>
    Task<GroupJoinLinkResponse?> CreateGroupJoinLinkAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts a group's link out, so the URLs already shared stop working.</summary>
    Task<bool> RevokeGroupJoinLinkAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a member or a guest out of a group. Either has to be square first.
    /// </summary>
    Task<bool> RemoveGroupMemberAsync(Guid groupId, Guid memberUserId, string memberName,
        CancellationToken cancellationToken = default);

    /// <summary>The caller taking themselves out, which needs a settled balance.</summary>
    Task<bool> LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

    Task<bool> UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest, CancellationToken cancellationToken = default);
    Task<bool> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);
    Task<bool> CreateTransactionWithReceiptAsync(CreateTransactionRequest request, IBrowserFile file,
        SaveReceiptRequest? receipt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a repayment either way round. <paramref name="otherName"/> is only for the
    /// message that follows: the request already says who and how much.
    /// </summary>
    Task<bool> SettleAsync(Guid groupId, SettleRequest request, string otherName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a repayment between two people in a group, neither of whom has to be the
    /// caller -- how a member writes down what two guests squared up between themselves.
    /// The names are only for the message that follows.
    /// </summary>
    Task<bool> RecordRepaymentAsync(Guid groupId, RecordRepaymentRequest request, string fromName,
        string toName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles the caller's own position in the selected group: every repayment between them
    /// and the rest of it, recorded together.
    /// </summary>
    Task<SettleUpResponse?> SettleUpAsync(SettleUpRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Puts the selected group down: it keeps everything and accepts nothing new.</summary>
    Task<bool> ArchiveGroupAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes it back up again.</summary>
    Task<bool> UnarchiveGroupAsync(CancellationToken cancellationToken = default);
}

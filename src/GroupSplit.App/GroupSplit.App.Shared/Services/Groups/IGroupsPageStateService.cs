using GroupSplit.Shared;
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


    event Action? OnGroupSelected;
    event Action? OnTransactionsChanged;
    event Action? OnGroupsChanged;

    Task IsReadyTask { get; }

    Task<bool> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>Who the selected group has asked to join and is still waiting on.</summary>
    Task<IReadOnlyList<GroupInvitationResponse>> GetGroupInvitationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks people to join by email. Nobody is added until they accept.</summary>
    Task<bool> InviteToGroupAsync(AddMemberRequest request, CancellationToken cancellationToken = default);

    Task<bool> WithdrawInvitationAsync(Guid invitationId, string email, CancellationToken cancellationToken = default);
    Task<bool> RemoveGroupMemberAsync(Guid memberUserId, string memberName, CancellationToken cancellationToken = default);

    /// <summary>The caller taking themselves out, which needs a settled balance.</summary>
    Task<bool> LeaveGroupAsync(CancellationToken cancellationToken = default);

    Task<bool> UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest, CancellationToken cancellationToken = default);
    Task<bool> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a repayment either way round. <paramref name="otherName"/> is only for the
    /// message that follows: the request already says who and how much.
    /// </summary>
    Task<bool> SettleAsync(SettleRequest request, string otherName, CancellationToken cancellationToken = default);

    /// <summary>Puts the selected group down: it keeps everything and accepts nothing new.</summary>
    Task<bool> ArchiveGroupAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes it back up again.</summary>
    Task<bool> UnarchiveGroupAsync(CancellationToken cancellationToken = default);
}

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

    ICollection<TransactionResponse> Transactions { get; }

    UserGroupBalanceResponse? Balance { get; }

    /// <summary>True while the selected group's expenses and balances are on their way.</summary>
    bool IsLoading { get; }


    event Action? OnGroupSelected;
    event Action? OnTransactionsChanged;
    event Action? OnGroupsChanged;

    Task IsReadyTask { get; }

    Task<bool> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default);
    Task<bool> AddGroupMemberAsync(AddMemberRequest request, CancellationToken cancellationToken = default);
    Task<bool> RemoveGroupMemberAsync(Guid memberUserId, CancellationToken cancellationToken = default);
    Task<bool> UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest, CancellationToken cancellationToken = default);
    Task<bool> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);
    Task<bool> SettleAsync(SettleRequest request, CancellationToken cancellationToken = default);
}

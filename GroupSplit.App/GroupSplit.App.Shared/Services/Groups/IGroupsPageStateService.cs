using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Groups;

public interface IGroupsPageStateService
{
    ICollection<GroupResponse> Groups { get; }
    
    GroupResponse? SelectedGroup { get; set; }
    
    ICollection<TransactionResponse> Transactions { get; }

    ICollection<GroupBalance> Balances { get; }


    event Action? OnGroupSelected;
    event Action? OnTransactionsChanged;
    event Action? OnGroupsChanged;
    
    Task IsReadyTask { get; }
    
    Task CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default);
    Task AddGroupMemberAsync(AddMemberRequest request, CancellationToken cancellationToken = default);
    Task RemoveGroupMemberAsync(Guid memberUserId, CancellationToken cancellationToken = default);
    Task UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest, CancellationToken cancellationToken = default);
    Task CreateTransactionAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);
 }
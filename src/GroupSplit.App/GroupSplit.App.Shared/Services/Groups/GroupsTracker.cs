using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Groups;

public class GroupsTracker
{
    [PersistentState] public ICollection<GroupResponse>? Groups { get; set; }

    [PersistentState] public GroupResponse? SelectedGroup { get; set; }

    [PersistentState] public ICollection<TransactionResponse> Transactions { get; set; } = [];
    [PersistentState] public UserGroupBalanceResponse? Balance { get; set; }

}
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Groups;

public class GroupsTracker
{
    [PersistentState] public ICollection<GroupResponse>? Groups { get; set; }

    [PersistentState] public GroupResponse? SelectedGroup { get; set; }

    [PersistentState] public PagedResponse<TransactionResponse>? Transactions { get; set; }
    [PersistentState] public UserGroupBalanceResponse? Balance { get; set; }

    /// <summary>
    /// Where this person stands across every group. Not about the selected group, and kept
    /// here anyway: it is read on the same announcements and shown beside the same figures.
    /// </summary>
    [PersistentState] public UserPositionResponse? Position { get; set; }
}
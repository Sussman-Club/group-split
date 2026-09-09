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
    /// Which group <see cref="Transactions"/> and <see cref="Balance"/> describe, or null
    /// when they describe nothing yet.
    /// </summary>
    /// <remarks>
    /// Kept beside them because the selection and the figures travel separately: the
    /// selection is set the moment a group page opens and the figures land when the server
    /// answers, and the prerender can persist this tracker in between -- with the group
    /// that was asked for beside the figures of the one selected before it. Without this
    /// the interactive side saw a selection that matched the URL and took the figures on
    /// trust, which is how one group's balances ended up on another group's page.
    /// </remarks>
    [PersistentState] public Guid? FiguresGroupId { get; set; }

    /// <summary>
    /// Where this person stands across every group. Not about the selected group, and kept
    /// here anyway: it is read on the same announcements and shown beside the same figures.
    /// </summary>
    [PersistentState] public UserPositionResponse? Position { get; set; }
}
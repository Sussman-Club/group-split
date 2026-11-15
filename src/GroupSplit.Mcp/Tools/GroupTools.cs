using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class GroupTools
{
    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "add_group_guests", ReadOnly = false, Destructive = false, Idempotent = false), Description("Add named guests to a group you can manage.")]
    public static Task<ICollection<GroupGuestResponse>> AddGroupGuests(GroupsClient client, Guid groupId, AddGuestsRequest guests, CancellationToken ct = default) =>
        client.AddGroupGuestsAsync(groupId, guests, ct);

    [McpServerTool(Name = "list_groups", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your groups, 100 per page.")]
    public static Task<ICollection<GroupResponse>> ListGroups(GroupsClient client, int page = 1, CancellationToken ct = default) => client.GetGroupsAsync(page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "get_group", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get a group you belong to.")]
    public static Task<GroupResponse> GetGroup(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.GetGroupAsync(groupId, ct);

    [McpServerTool(Name = "group_members", ReadOnly = true, Destructive = false, Idempotent = true), Description("List members and guests of a group.")]
    public static Task<ICollection<UserInfo>> GroupMembers(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.GetGroupMembersAsync(groupId, ct);

    [McpServerTool(Name = "group_balances", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get balances and settlement plan for a group.")]
    public static Task<UserGroupBalanceResponse> GroupBalances(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.GetGroupUserBalanceAsync(groupId, cancellationToken: ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_group_archived", ReadOnly = false, Destructive = false, Idempotent = true), Description("Archive or unarchive a group in your own list.")]
    public static Task<GroupResponse> SetGroupArchived(GroupsClient client, Guid groupId, bool archived, CancellationToken ct = default) => archived ? client.ArchiveGroupAsync(groupId, ct) : client.UnarchiveGroupAsync(groupId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_group", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a group for the signed-in user.")]
    public static Task<GroupResponse> CreateGroup(GroupsClient client, string name, string description = "", CancellationToken ct = default) => client.CreateGroupAsync(new CreateGroupRequest { Name = name, Description = description }, ct);

    [McpServerTool(Name = "group_activity", ReadOnly = true, Destructive = false, Idempotent = true), Description("List expenses and repayments in a group you belong to, newest first.")]
    public static Task<PagedResponseOfGroupLedgerEntryResponse> GroupActivity(GroupsClient client, Guid groupId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? search = null, int page = 1, CancellationToken ct = default) =>
        client.GetGroupActivityAsync(groupId, from: from, to: to, search: search!, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "group_spending", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize a group's spending by category.")]
    public static Task<CategorySpendingSummaryResponse> GroupSpending(GroupsClient client, Guid groupId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidBy = null, CancellationToken ct = default) =>
        client.GetGroupSpendingByCategoryAsync(groupId, from: from, to: to, paidBy: paidBy, cancellationToken: ct);

    [McpServerTool(Name = "group_guests", ReadOnly = true, Destructive = false, Idempotent = true), Description("List guests in a group you can access.")]
    public static Task<ICollection<GroupGuestResponse>> GroupGuests(GroupsClient client, Guid groupId, CancellationToken ct = default) =>
        client.GetGroupGuestsAsync(groupId, ct);
}

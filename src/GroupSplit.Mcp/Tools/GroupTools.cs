using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class GroupTools
{
    [McpServerTool(Name = "list_supported_currencies", ReadOnly = true, Destructive = false, Idempotent = true), Description("List the supported currency codes for groups, expenses and repayments. No conversion is performed.")]
    public static IReadOnlyList<string> ListSupportedCurrencies() => CurrencyPrecision.Codes;

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "add_group_guests", ReadOnly = false, Destructive = false, Idempotent = false), Description("Add named guests to a group you can manage.")]
    public static Task<ICollection<GroupGuestResponse>> AddGroupGuests(GroupsClient client, Guid groupId, AddGuestsRequest guests, CancellationToken ct = default) =>
        client.AddGroupGuestsAsync(groupId, guests, ct);

    [McpServerTool(Name = "list_groups", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your groups, 100 per page, optionally filtering by archived status.")]
    public static Task<ICollection<GroupResponse>> ListGroups(GroupsClient client, int page = 1, bool? archived = null, CancellationToken ct = default) => client.GetGroupsAsync(page: Math.Max(1, page), pageSize: 100, archived: archived, cancellationToken: ct);

    [McpServerTool(Name = "get_group", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get a group you belong to.")]
    public static Task<GroupResponse> GetGroup(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.GetGroupAsync(groupId, ct);

    [McpServerTool(Name = "group_members", ReadOnly = true, Destructive = false, Idempotent = true), Description("List members and guests of a group.")]
    public static Task<ICollection<UserInfo>> GroupMembers(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.GetGroupMembersAsync(groupId, ct);

    [McpServerTool(Name = "group_balances", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get balances and settlement plan for a group, optionally through a date with a UTC offset in minutes.")]
    public static Task<UserGroupBalanceResponse> GroupBalances(GroupsClient client, Guid groupId, DateOnly? through = null, int? utcOffset = null, CancellationToken ct = default) => client.GetGroupUserBalanceAsync(groupId, through, utcOffset, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_group_archived", ReadOnly = false, Destructive = false, Idempotent = true), Description("Archive or unarchive a group in your own list.")]
    public static Task<GroupResponse> SetGroupArchived(GroupsClient client, Guid groupId, bool archived, CancellationToken ct = default) => archived ? client.ArchiveGroupAsync(groupId, ct) : client.UnarchiveGroupAsync(groupId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_group", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a group. Currency is its default for new expenses; omit it for USD. Other supported currencies can still be used per expense.")]
    public static Task<GroupResponse> CreateGroup(GroupsClient client, string name, string description = "", string currency = "USD", CancellationToken ct = default) => client.CreateGroupAsync(new CreateGroupRequest { Name = name, Description = description, Currency = currency }, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_group_default_currency", ReadOnly = false, Destructive = false, Idempotent = true), Description("Set the supported default currency for new group expenses. Existing expenses retain their currency; no amounts are converted.")]
    public static Task<GroupResponse> SetGroupDefaultCurrency(GroupsClient client, Guid groupId, string currency, CancellationToken ct = default)
    {
        var patch = new JsonPatchDocument<CreateGroupRequest>();
        patch.Replace(request => request.Currency, currency);
        return client.UpdateGroupAsync(groupId, patch, ct);
    }

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "rename_group", ReadOnly = false, Destructive = false, Idempotent = true), Description("Rename a group and optionally replace its description. Omit description to keep it unchanged.")]
    public static Task<GroupResponse> RenameGroup(GroupsClient client, Guid groupId, string name, string? description = null, CancellationToken ct = default)
    {
        var patch = new JsonPatchDocument<CreateGroupRequest>();
        patch.Replace(request => request.Name, name);
        if (description is not null)
        {
            patch.Replace(request => request.Description, description);
        }
        return client.UpdateGroupAsync(groupId, patch, ct);
    }

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_group_default_split", ReadOnly = false, Destructive = false, Idempotent = true), Description("Set the default split rule for uncategorized expenses in a group.")]
    public static Task<GroupResponse> SetGroupDefaultSplit(GroupsClient client, Guid groupId, Guid ruleId, CancellationToken ct = default)
    {
        var patch = new JsonPatchDocument<CreateGroupRequest>();
        patch.Replace(request => request.DefaultSplitRuleId, ruleId);
        return client.UpdateGroupAsync(groupId, patch, ct);
    }

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_group_default_period", ReadOnly = false, Destructive = false, Idempotent = true), Description("Set the default activity display period for a group.")]
    public static Task<GroupResponse> SetGroupDefaultPeriod(GroupsClient client, Guid groupId, ActivityPeriod period, CancellationToken ct = default)
    {
        var patch = new JsonPatchDocument<CreateGroupRequest>();
        patch.Replace(request => request.DefaultPeriod, period);
        return client.UpdateGroupAsync(groupId, patch, ct);
    }

    [McpServerTool(Name = "group_activity", ReadOnly = true, Destructive = false, Idempotent = true), Description("List expenses and repayments in a group you belong to, newest first.")]
    public static Task<PagedResponseOfGroupLedgerEntryResponse> GroupActivity(GroupsClient client, Guid groupId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? search = null, int page = 1, int? kind = null, Guid? categoryId = null, bool? uncategorized = null, Guid? paidBy = null, string? sortBy = null, bool? sortDescending = null, CancellationToken ct = default) =>
        client.GetGroupActivityAsync(groupId, from: from, to: to, kind: kind, search: search!, categoryId: categoryId, uncategorized: uncategorized, paidBy: paidBy, sortBy: sortBy!, sortDescending: sortDescending, page: Math.Max(1, page), pageSize: 100, cancellationToken: ct);

    [McpServerTool(Name = "group_spending", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize a group's spending by category.")]
    public static Task<CategorySpendingSummaryResponse> GroupSpending(GroupsClient client, Guid groupId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidBy = null, Guid? categoryId = null, bool? uncategorized = null, string? search = null, string? category = null, CancellationToken ct = default) =>
        client.GetGroupSpendingByCategoryAsync(groupId, from: from, to: to, paidBy: paidBy, categoryId: categoryId, uncategorized: uncategorized, search: search!, category: category!, cancellationToken: ct);

    [McpServerTool(Name = "group_expense_summary", ReadOnly = true, Destructive = false, Idempotent = true), Description("Summarize a group's expenses with optional filters.")]
    public static Task<TransactionSummaryResponse> GroupExpenseSummary(GroupsClient client, Guid groupId, DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? paidBy = null, Guid? categoryId = null, bool? uncategorized = null, string? search = null, string? category = null, CancellationToken ct = default) =>
        client.GetGroupTransactionsSummaryAsync(groupId, from, to, paidBy, categoryId, uncategorized, search!, category!, ct);

    [McpServerTool(Name = "group_guests", ReadOnly = true, Destructive = false, Idempotent = true), Description("List guests in a group you can access.")]
    public static Task<ICollection<GroupGuestResponse>> GroupGuests(GroupsClient client, Guid groupId, CancellationToken ct = default) =>
        client.GetGroupGuestsAsync(groupId, ct);

    [McpServerTool(Name = "group_past_members", ReadOnly = true, Destructive = false, Idempotent = true), Description("List former members of an accessible group.")]
    public static Task<ICollection<UserInfo>> GroupPastMembers(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.GetGroupPastMembersAsync(groupId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "leave_group", ReadOnly = false, Destructive = true, Idempotent = false), Description("Leave a group after explicit confirmation. The API requires your balance to be settled.")]
    public static Task LeaveGroup(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.LeaveGroupAsync(groupId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "remove_group_member", ReadOnly = false, Destructive = true, Idempotent = false), Description("Remove a member or guest from a group after user confirmation; the API enforces membership and balance rules.")]
    public static Task<GroupResponse> RemoveGroupMember(GroupsClient client, Guid groupId, Guid userId, CancellationToken ct = default) => client.RemoveGroupMemberAsync(groupId, userId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "issue_guest_link", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a link for a guest to claim their group position. The link grants access and should only be shared deliberately.")]
    public static Task<GroupGuestResponse> IssueGuestLink(GroupsClient client, Guid groupId, Guid userId, IssueGuestLinkRequest request, CancellationToken ct = default) => client.IssueGuestLinkAsync(groupId, userId, request, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "revoke_guest_link", ReadOnly = false, Destructive = true, Idempotent = false), Description("Revoke a guest's claim link after confirmation; the guest and their records remain.")]
    public static Task RevokeGuestLink(GroupsClient client, Guid groupId, Guid userId, CancellationToken ct = default) => client.RevokeGuestLinkAsync(groupId, userId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "invite_to_group", ReadOnly = false, Destructive = false, Idempotent = false), Description("Invite people to a group. Invitation links grant access and must be shared deliberately.")]
    public static Task<ICollection<GroupInvitationResponse>> InviteToGroup(GroupsClient client, Guid groupId, InviteToGroupRequest invitation, CancellationToken ct = default) => client.InviteToGroupAsync(groupId, invitation, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "withdraw_group_invitation", ReadOnly = false, Destructive = true, Idempotent = false), Description("Withdraw an outstanding invitation after user confirmation.")]
    public static Task<InvitationClosedResponse> WithdrawGroupInvitation(GroupsClient client, Guid groupId, Guid invitationId, CancellationToken ct = default) => client.WithdrawGroupInvitationAsync(groupId, invitationId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "revoke_group_join_links", ReadOnly = false, Destructive = true, Idempotent = false), Description("Revoke a group's join links after confirmation, preventing future use.")]
    public static Task RevokeGroupJoinLinks(GroupsClient client, Guid groupId, CancellationToken ct = default) => client.RevokeGroupJoinLinksAsync(groupId, ct);
}

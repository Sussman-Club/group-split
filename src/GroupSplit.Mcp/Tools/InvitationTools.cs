using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class InvitationTools
{
    [McpServerTool(Name = "list_invitations", ReadOnly = true, Destructive = false, Idempotent = true), Description("List invitations you have opened and not yet answered. Invitation tokens grant access and should only be shared deliberately.")]
    public static Task<ICollection<GroupInvitationResponse>> ListInvitations(InvitationsClient client, CancellationToken ct = default) =>
        client.GetMyInvitationsAsync(ct);

    [McpServerTool(Name = "get_invitation", ReadOnly = true, Destructive = false, Idempotent = true), Description("Inspect an invitation token without claiming it.")]
    public static Task<InvitationClaimResponse> GetInvitation(InvitationsClient client, string token, CancellationToken ct = default) =>
        client.GetInvitationClaimAsync(TokenIn(token), ct);

    [McpServerTool(Name = "get_join_link", ReadOnly = true, Destructive = false, Idempotent = true), Description("Inspect a join link without joining the group.")]
    public static Task<JoinLinkResponse> GetJoinLink(InvitationsClient client, string token, CancellationToken ct = default) =>
        client.GetJoinLinkAsync(TokenIn(token), ct);

    [McpServerTool(Name = "list_group_join_links", ReadOnly = true, Destructive = false, Idempotent = true), Description("List active join links for a group. Anyone holding a link can use it to join, so share it deliberately.")]
    public static Task<ICollection<GroupJoinLinkResponse>> ListGroupJoinLinks(GroupsClient client, Guid groupId, CancellationToken ct = default) =>
        client.GetGroupJoinLinksAsync(groupId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "claim_invitation", ReadOnly = false, Destructive = true, Idempotent = false), Description("Claim a guest invitation after inspecting it and obtaining user confirmation. The guest's expenses, shares, and rule memberships become yours; some repayments may be cancelled.")]
    public static Task<InvitationClaimedResponse> ClaimInvitation(InvitationsClient client, string token, CancellationToken ct = default) =>
        client.ClaimInvitationAsync(TokenIn(token), ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "decline_invitation", ReadOnly = false, Destructive = true, Idempotent = false), Description("Decline a guest invitation after user confirmation; its link stops working and the guest remains in the group.")]
    public static Task<InvitationClosedResponse> DeclineInvitation(InvitationsClient client, string token, CancellationToken ct = default) =>
        client.DeclineInvitationAsync(TokenIn(token), ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "join_group_by_link", ReadOnly = false, Destructive = false, Idempotent = true), Description("Join a group as yourself using its join link; inspect the link and confirm the group with the user first.")]
    public static Task<JoinedGroupResponse> JoinGroupByLink(InvitationsClient client, string token, CancellationToken ct = default) =>
        client.AcceptJoinLinkAsync(TokenIn(token), ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_group_join_link", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a group join link. Anyone holding the link may join, so only share it deliberately.")]
    public static Task<GroupJoinLinkResponse> CreateGroupJoinLink(GroupsClient client, Guid groupId, CancellationToken ct = default) =>
        client.CreateGroupJoinLinkAsync(groupId, ct);

    private static string TokenIn(string value)
    {
        var text = value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return text;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? text : segments[^1];
    }
}

using System.ComponentModel;
using GroupSplit.Shared;
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

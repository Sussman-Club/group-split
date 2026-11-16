using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class GroupInvitationTools
{
    [McpServerTool(Name = "group_invitations", ReadOnly = true, Destructive = false, Idempotent = true), Description("List a group's invitations. Invitation tokens grant access and should only be shared deliberately.")]
    public static Task<ICollection<GroupInvitationResponse>> GroupInvitations(GroupsClient client, Guid groupId, CancellationToken ct = default) =>
        client.GetGroupInvitationsAsync(groupId, ct);
}

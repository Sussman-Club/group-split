using System.ComponentModel;
using GroupSplit.Shared;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class AccountTools
{
    [McpServerTool(Name = "current_user", ReadOnly = true, Destructive = false, Idempotent = true),
     Description("Show the signed-in account.")]
    public static Task<UserInfo> CurrentUser(UsersClient client, CancellationToken ct = default) =>
        client.GetCurrentUserAsync(ct);

    [McpServerTool(Name = "user_position", ReadOnly = true, Destructive = false, Idempotent = true),
     Description("Show your position across groups.")]
    public static Task<UserPositionResponse> UserPosition(UsersClient client, CancellationToken ct = default) =>
        client.GetCurrentUserPositionAsync(ct);
}

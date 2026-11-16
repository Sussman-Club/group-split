using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
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

    [McpServerTool(Name = "user_activity", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your recent activity across groups, 100 per page.")]
    public static Task<PagedResponseOfUserActivityResponse> UserActivity(UsersClient client, int page = 1, CancellationToken ct = default) => client.GetCurrentUserActivityAsync(Math.Max(1, page), 100, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_user_language", ReadOnly = false, Destructive = false, Idempotent = true), Description("Set the signed-in user's language preference.")]
    public static Task SetUserLanguage(UsersClient client, LanguagePreferenceRequest preference, CancellationToken ct = default) => client.SetCurrentUserLanguageAsync(preference, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "dismiss_onboarding", ReadOnly = false, Destructive = false, Idempotent = true), Description("Dismiss one onboarding step for the signed-in user.")]
    public static Task DismissOnboarding(UsersClient client, string part, CancellationToken ct = default) => client.DismissOnboardingAsync(part, ct);
}

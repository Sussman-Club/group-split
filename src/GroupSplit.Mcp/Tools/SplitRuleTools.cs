using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SplitRuleTools
{
    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_split_rule", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a split rule for an accessible group.")]
    public static Task<SplitRuleDetailsResponse> CreateSplitRule(SplitRulesClient client, CreateSplitRuleRequest rule, CancellationToken ct = default) =>
        client.CreateSplitRuleAsync(rule, ct);

    [McpServerTool(Name = "list_split_rules", ReadOnly = true, Destructive = false, Idempotent = true), Description("List split rules available to you, optionally for one group.")]
    public static Task<ICollection<SplitRuleResponse>> ListSplitRules(SplitRulesClient client, Guid? groupId = null, CancellationToken ct = default) =>
        client.GetSplitRulesAsync(groupId, ct);

    [McpServerTool(Name = "get_split_rule", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get an accessible split rule and its participants.")]
    public static Task<SplitRuleDetailsResponse> GetSplitRule(SplitRulesClient client, Guid ruleId, CancellationToken ct = default) =>
        client.GetSplitRuleAsync(ruleId, ct);

    [McpServerTool(Name = "split_rule_history", ReadOnly = true, Destructive = false, Idempotent = true), Description("Show the versions of an accessible split rule.")]
    public static Task<SplitRuleHistoryResponse> SplitRuleHistory(SplitRulesClient client, Guid ruleId, CancellationToken ct = default) =>
        client.GetSplitRuleVersionsAsync(ruleId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "update_split_rule", ReadOnly = false, Destructive = false, Idempotent = false), Description("Update a split rule's name and complete definition. Review changes with the user first; this can create a new rule version.")]
    public static Task<SplitRuleDetailsResponse> UpdateSplitRule(SplitRulesClient client, Guid ruleId, UpdateSplitRuleRequest rule, CancellationToken ct = default) =>
        client.UpdateSplitRuleAsync(ruleId, rule, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_split_rule", ReadOnly = false, Destructive = true, Idempotent = false), Description("Permanently delete a split rule and its versions after the user confirms. The API may refuse deletion when the rule is in use.")]
    public static async Task DeleteSplitRule(SplitRulesClient client, Guid ruleId, CancellationToken ct = default) =>
        await client.DeleteSplitRuleAsync(ruleId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_split_rule_versions", ReadOnly = false, Destructive = true, Idempotent = false), Description("Replace a split rule's version history after reviewing all versions and obtaining user confirmation. The API validates historical use.")]
    public static Task<SplitRuleHistoryResponse> SetSplitRuleVersions(SplitRulesClient client, Guid ruleId, IReadOnlyList<SplitRuleVersionInput> versions, CancellationToken ct = default) => client.SetSplitRuleVersionsAsync(ruleId, versions, ct);
}

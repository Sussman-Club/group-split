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
}

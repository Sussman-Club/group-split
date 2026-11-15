using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SettlementTools
{
    [McpServerTool(Name = "settlement_plan", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview your settlement plan across groups without recording repayments. Optional through date uses utcOffset minutes.")]
    public static Task<SettlementPlanResponse> SettlementPlan(UsersClient client, DateOnly? through = null, int? utcOffset = null, CancellationToken ct = default) =>
        client.GetSettlementPlanAsync(through, utcOffset, ct);

    [McpServerTool(Name = "list_settlements", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your recorded settlements, 100 per page.")]
    public static Task<PagedResponseOfSettlementResponse> ListSettlements(UsersClient client, int page = 1, CancellationToken ct = default) =>
        client.GetSettlementsAsync(Math.Max(1, page), 100, ct);
}

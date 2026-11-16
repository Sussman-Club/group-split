using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SettlementTools
{
    [McpServerTool(Name = "settlement_plan", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview your settlement plan across groups without recording repayments. Amounts and suggestions are separate per currency and must not be added together. Optional through date uses utcOffset minutes.")]
    public static Task<SettlementPlanResponse> SettlementPlan(UsersClient client, DateOnly? through = null, int? utcOffset = null, CancellationToken ct = default) =>
        client.GetSettlementPlanAsync(through, utcOffset, ct);

    [McpServerTool(Name = "list_settlements", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your recorded settlements, 100 per page.")]
    public static Task<PagedResponseOfSettlementResponse> ListSettlements(UsersClient client, int page = 1, CancellationToken ct = default) =>
        client.GetSettlementsAsync(Math.Max(1, page), 100, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "settle_with_person", ReadOnly = false, Destructive = true, Idempotent = false), Description("Record one payment in one currency between you and another person across groups. First use settlement_plan, specify the matching currency, review the exact amount and direction, and obtain user confirmation. Never combine currencies. Repeating can duplicate a payment.")]
    public static Task<SettleWithPersonResponse> SettleWithPerson(UsersClient client, SettleWithPersonRequest payment, CancellationToken ct = default) =>
        client.SettleWithPersonAsync(payment, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "settle_group_debt", ReadOnly = false, Destructive = true, Idempotent = false), Description("Record a group repayment after reviewing balances and obtaining user confirmation. Repeating may duplicate payment records.")]
    public static Task SettleGroupDebt(GroupsClient client, Guid groupId, SettleRequest payment, CancellationToken ct = default) => client.SettleGroupDebtsAsync(groupId, payment, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "record_group_repayment", ReadOnly = false, Destructive = true, Idempotent = false), Description("Record a repayment between group members after user confirmation; repeating may duplicate the payment.")]
    public static Task<SettlementPayment> RecordGroupRepayment(GroupsClient client, Guid groupId, RecordRepaymentRequest payment, CancellationToken ct = default) => client.RecordRepaymentAsync(groupId, payment, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "settle_up_group", ReadOnly = false, Destructive = true, Idempotent = false), Description("Record group settlement payments after reviewing the plan and obtaining explicit confirmation. Repeating may duplicate payments.")]
    public static Task<SettleUpResponse> SettleUpGroup(GroupsClient client, Guid groupId, SettleUpRequest payment, CancellationToken ct = default) => client.SettleUpAsync(groupId, payment, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_settlement_request", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a shareable request for a group repayment. The link is sensitive and must only be shared deliberately.")]
    public static Task<SettlementRequestResponse> CreateSettlementRequest(GroupsClient client, Guid groupId, CreateSettlementRequest request, CancellationToken ct = default) => client.CreateGroupSettlementRequestAsync(groupId, request, ct);

    [McpServerTool(Name = "get_settlement_request", ReadOnly = true, Destructive = false, Idempotent = true), Description("Inspect a settlement request by token before acting on it.")]
    public static Task<SettlementRequestResponse> GetSettlementRequest(Settlement_requestsClient client, string token, CancellationToken ct = default) => client.GetSettlementRequestAsync(token, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "cancel_settlement_request", ReadOnly = false, Destructive = true, Idempotent = false), Description("Cancel a settlement request after user confirmation.")]
    public static Task<SettlementRequestResponse> CancelSettlementRequest(Settlement_requestsClient client, string token, CancellationToken ct = default) => client.CancelSettlementRequestAsync(token, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "record_settlement_request_repayment", ReadOnly = false, Destructive = true, Idempotent = false), Description("Record the repayment on a settlement request after inspecting its amount, parties and group and obtaining user confirmation. This changes balances.")]
    public static Task<SettlementRequestResponse> RecordSettlementRequestRepayment(Settlement_requestsClient client, string token, CancellationToken ct = default) => client.RecordSettlementRequestRepaymentAsync(token, ct);
}

using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SupportTools
{
    [McpServerTool(Name = "list_support_reports", ReadOnly = true, Destructive = false, Idempotent = true), Description("List support reports belonging to the signed-in user.")]
    public static Task<ICollection<SupportReportResponse>> ListReports(Support_reportsClient client, CancellationToken ct = default) => client.ListSupportReportsAsync(ct);

    [McpServerTool(Name = "support_report_messages", ReadOnly = true, Destructive = false, Idempotent = true), Description("Read public messages on one of your support reports. Page is zero-based.")]
    public static Task<ICollection<SupportMessageResponse>> Messages(Support_reportsClient client, Guid reportId, int page = 0, CancellationToken ct = default) => client.MessagesAllAsync(reportId, page, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "reply_to_support_report", ReadOnly = false, Destructive = false, Idempotent = false), Description("Send a public reply on your support report after the user approves the message.")]
    public static Task<SupportMessageResponse> Reply(Support_reportsClient client, Guid reportId, string body, CancellationToken ct = default) => client.MessagesAsync(reportId, new CreateSupportMessageRequest { Body = body }, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_support_report", ReadOnly = false, Destructive = false, Idempotent = false), Description("Submit a support report after the user approves its contents.")]
    public static Task<SupportReportResponse> CreateReport(Support_reportsClient client, CreateSupportReportRequest report, CancellationToken ct = default) => client.CreateSupportReportAsync(report, ct);
}

using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class BankConnectionTools
{
    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "refresh_bank_connection", ReadOnly = false, Destructive = false, Idempotent = false), Description("Refresh a linked bank connection's accounts and queue a transaction sync.")]
    public static Task<BankConnectionResponse> RefreshBankConnection(BankConnectionsClient client, Guid connectionId, CancellationToken ct = default) =>
        client.RefreshBankConnectionAsync(connectionId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "sync_bank_connection", ReadOnly = false, Destructive = false, Idempotent = false), Description("Queue a transaction sync for a linked bank connection. The sync runs asynchronously.")]
    public static async Task<string> SyncBankConnection(BankConnectionsClient client, Guid connectionId, CancellationToken ct = default)
    {
        await client.SyncBankConnectionAsync(connectionId, ct);
        return "queued";
    }

    [McpServerTool(Name = "list_linked_banks", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your linked banks and accounts.")]
    public static Task<BankConnectionsResponse> ListLinkedBanks(BankConnectionsClient client, CancellationToken ct = default) =>
        client.GetBankConnectionsAsync(ct);
}

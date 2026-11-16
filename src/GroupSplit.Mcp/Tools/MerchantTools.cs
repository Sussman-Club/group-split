using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class MerchantTools
{
    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_merchant", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a merchant.")]
    public static Task<MerchantResponse> CreateMerchant(MerchantsClient client, CreateMerchantRequest merchant, CancellationToken ct = default) =>
        client.CreateMerchantAsync(merchant, ct);

    [McpServerTool(Name = "list_merchants", ReadOnly = true, Destructive = false, Idempotent = true), Description("List merchants, optionally filtered by search text.")]
    public static Task<ICollection<MerchantResponse>> ListMerchants(MerchantsClient client, string? search = null, CancellationToken ct = default) =>
        client.GetMerchantsAsync(search!, ct);

    [McpServerTool(Name = "get_merchant", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get a merchant by ID.")]
    public static Task<MerchantResponse> GetMerchant(MerchantsClient client, Guid merchantId, CancellationToken ct = default) =>
        client.GetMerchantAsync(merchantId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "update_merchant", ReadOnly = false, Destructive = false, Idempotent = false), Description("Update a merchant through the ordinary merchant API. Shared-directory changes are currently refused by that API; use staff tools if authorized.")]
    public static Task<MerchantResponse> UpdateMerchant(MerchantsClient client, Guid merchantId, UpdateMerchantRequest merchant, CancellationToken ct = default) => client.UpdateMerchantAsync(merchantId, merchant, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "delete_merchant", ReadOnly = false, Destructive = true, Idempotent = false), Description("Delete a merchant after user confirmation. The ordinary merchant API currently refuses deletion of shared merchants.")]
    public static Task DeleteMerchant(MerchantsClient client, Guid merchantId, CancellationToken ct = default) => client.DeleteMerchantAsync(merchantId, ct);
}

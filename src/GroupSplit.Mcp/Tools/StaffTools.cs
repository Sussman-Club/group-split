using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

// Staff routes are not included in the generated client. These fixed routes use the
// same API HttpClient and exchange the caller's token; the API enforces staff roles.
[McpServerToolType]
public sealed class StaffTools
{
    [McpServerTool(Name = "staff_team", ReadOnly = true, Destructive = false, Idempotent = true), Description("List staff available for ticket assignment; requires the API's staff role.")]
    public static Task<JsonElement?> Team(IHttpClientFactory clients, CancellationToken ct = default) => Send(clients, HttpMethod.Get, "staff/team", null, ct);

    [McpServerTool(Name = "staff_tickets", ReadOnly = true, Destructive = false, Idempotent = true), Description("List staff support tickets with optional filters; page is zero-based.")]
    public static Task<JsonElement?> Tickets(IHttpClientFactory clients, string? search = null, string? status = null, string? priority = null, string? type = null, string? assignee = null, string? sort = null, bool needsReview = false, int page = 0, CancellationToken ct = default) =>
        Send(clients, HttpMethod.Get, "staff/tickets" + Query(("search", search), ("status", status), ("priority", priority), ("type", type), ("assignee", assignee), ("sort", sort), ("needsReview", needsReview ? "true" : null), ("page", Math.Max(0, page).ToString())), null, ct);

    [McpServerTool(Name = "staff_ticket", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get a support ticket as staff.")]
    public static Task<JsonElement?> Ticket(IHttpClientFactory clients, Guid ticketId, CancellationToken ct = default) => Send(clients, HttpMethod.Get, $"staff/tickets/{ticketId:D}", null, ct);

    [McpServerTool(Name = "staff_ticket_messages", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get a ticket's public messages and staff notes.")]
    public static Task<JsonElement?> TicketMessages(IHttpClientFactory clients, Guid ticketId, CancellationToken ct = default) => Send(clients, HttpMethod.Get, $"staff/tickets/{ticketId:D}/messages", null, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_reply_ticket", ReadOnly = false, Destructive = false, Idempotent = false), Description("Post a public support reply after user approval. Include the ticket's expected updated timestamp to detect concurrent changes.")]
    public static Task<JsonElement?> Reply(IHttpClientFactory clients, Guid ticketId, CreateSupportMessageRequest reply, CancellationToken ct = default) => Send(clients, HttpMethod.Post, $"staff/tickets/{ticketId:D}/reply", reply, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_add_ticket_note", ReadOnly = false, Destructive = false, Idempotent = false), Description("Add an internal staff-only ticket note.")]
    public static Task<JsonElement?> AddNote(IHttpClientFactory clients, Guid ticketId, string body, CancellationToken ct = default) => Send(clients, HttpMethod.Post, $"staff/tickets/{ticketId:D}/notes", new CreateSupportNoteRequest(body), ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_update_ticket", ReadOnly = false, Destructive = false, Idempotent = false), Description("Update a support ticket's classification, assignment or status. Supply the reviewed current version to detect conflicts.")]
    public static Task<JsonElement?> UpdateTicket(IHttpClientFactory clients, Guid ticketId, UpdateSupportTicketRequest ticket, CancellationToken ct = default) => Send(clients, HttpMethod.Put, $"staff/tickets/{ticketId:D}", ticket, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_bulk_update_tickets", ReadOnly = false, Destructive = true, Idempotent = false), Description("Update multiple tickets atomically after reviewing every ticket, version and intended change and obtaining explicit confirmation.")]
    public static Task<JsonElement?> BulkTickets(IHttpClientFactory clients, BulkTicketUpdateRequest update, CancellationToken ct = default) => Send(clients, HttpMethod.Post, "staff/tickets/bulk", update, ct);

    [McpServerTool(Name = "staff_users", ReadOnly = true, Destructive = false, Idempotent = true), Description("Search the staff user directory; page is zero-based.")]
    public static Task<JsonElement?> Users(IHttpClientFactory clients, string? search = null, int page = 0, CancellationToken ct = default) => Send(clients, HttpMethod.Get, "staff/users/directory" + Query(("search", search), ("page", Math.Max(0, page).ToString())), null, ct);

    [McpServerTool(Name = "staff_user", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get staff support context for an account.")]
    public static Task<JsonElement?> User(IHttpClientFactory clients, Guid userId, CancellationToken ct = default) => Send(clients, HttpMethod.Get, $"staff/users/{userId:D}", null, ct);

    [McpServerTool(Name = "staff_activity", ReadOnly = true, Destructive = false, Idempotent = true), Description("Read staff activity history with optional resource filters.")]
    public static Task<JsonElement?> Activity(IHttpClientFactory clients, string? resource = null, Guid? resourceId = null, int page = 0, CancellationToken ct = default) => Send(clients, HttpMethod.Get, "staff/activity" + Query(("resource", resource), ("resourceId", resourceId?.ToString()), ("page", Math.Max(0, page).ToString())), null, ct);

    [McpServerTool(Name = "staff_views", ReadOnly = true, Destructive = false, Idempotent = true), Description("List your saved staff views for Tickets or Merchants.")]
    public static Task<JsonElement?> Views(IHttpClientFactory clients, string resource, CancellationToken ct = default) => Send(clients, HttpMethod.Get, "staff/views" + Query(("resource", resource)), null, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_save_view", ReadOnly = false, Destructive = false, Idempotent = false), Description("Save a staff ticket or merchant view.")]
    public static Task<JsonElement?> SaveView(IHttpClientFactory clients, SaveStaffViewRequest view, CancellationToken ct = default) => Send(clients, HttpMethod.Post, "staff/views", view, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_delete_view", ReadOnly = false, Destructive = true, Idempotent = false), Description("Delete a saved staff view after user confirmation.")]
    public static Task<JsonElement?> DeleteView(IHttpClientFactory clients, Guid viewId, CancellationToken ct = default) => Send(clients, HttpMethod.Delete, $"staff/views/{viewId:D}", null, ct);

    [McpServerTool(Name = "staff_merchants", ReadOnly = true, Destructive = false, Idempotent = true), Description("Search the shared merchant directory as staff.")]
    public static Task<JsonElement?> Merchants(IHttpClientFactory clients, string? search = null, string? sort = null, int page = 0, CancellationToken ct = default) => Send(clients, HttpMethod.Get, "staff/merchants" + Query(("search", search), ("sort", sort), ("page", Math.Max(0, page).ToString())), null, ct);

    [McpServerTool(Name = "staff_merchant", ReadOnly = true, Destructive = false, Idempotent = true), Description("Get a shared merchant as staff.")]
    public static Task<JsonElement?> Merchant(IHttpClientFactory clients, Guid merchantId, CancellationToken ct = default) => Send(clients, HttpMethod.Get, $"staff/merchants/{merchantId:D}", null, ct);

    [McpServerTool(Name = "staff_merchant_aliases", ReadOnly = true, Destructive = false, Idempotent = true), Description("List a shared merchant's import aliases.")]
    public static Task<JsonElement?> Aliases(IHttpClientFactory clients, Guid merchantId, CancellationToken ct = default) => Send(clients, HttpMethod.Get, $"staff/merchants/{merchantId:D}/aliases", null, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_create_merchant", ReadOnly = false, Destructive = false, Idempotent = false), Description("Create a shared merchant as staff.")]
    public static Task<JsonElement?> CreateMerchant(IHttpClientFactory clients, CreateMerchantRequest merchant, CancellationToken ct = default) => Send(clients, HttpMethod.Post, "staff/merchants", merchant, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_update_merchant", ReadOnly = false, Destructive = false, Idempotent = false), Description("Update a shared merchant as staff. Supply its reviewed current version to detect conflicts.")]
    public static Task<JsonElement?> UpdateMerchant(IHttpClientFactory clients, Guid merchantId, UpdateMerchantRequest merchant, CancellationToken ct = default) => Send(clients, HttpMethod.Put, $"staff/merchants/{merchantId:D}", merchant, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_upload_merchant_logo", ReadOnly = false, Destructive = false, Idempotent = false), Description("Upload a PNG logo of at most 1 MiB for a shared merchant. Supply its name and current version from staff_merchant, and the image bytes as base64.")]
    public static async Task<JsonElement?> UploadMerchantLogo(IHttpClientFactory clients, Guid merchantId, string name, Guid expectedVersion, string contentBase64, CancellationToken ct = default)
    {
        if (contentBase64.Length > ((MerchantLogos.MaximumUploadBytes + 2) / 3) * 4)
        {
            throw new ArgumentException("Logo exceeds 1 MiB.", nameof(contentBase64));
        }

        var bytes = Convert.FromBase64String(contentBase64);
        if (bytes.Length is 0 or > MerchantLogos.MaximumUploadBytes)
        {
            throw new ArgumentException("Logo must contain between 1 byte and 1 MiB.", nameof(contentBase64));
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(merchantId.ToString()), "merchantId");
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(expectedVersion.ToString()), "expectedVersion");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");
        using var client = clients.CreateClient("api");
        using var response = await client.PostAsync("staff/merchant-images", form, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_add_merchant_alias", ReadOnly = false, Destructive = false, Idempotent = false), Description("Add an import alias to a shared merchant.")]
    public static Task<JsonElement?> AddAlias(IHttpClientFactory clients, Guid merchantId, string name, CancellationToken ct = default) => Send(clients, HttpMethod.Post, $"staff/merchants/{merchantId:D}/aliases", new CreateMerchantAliasRequest(name), ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_remove_merchant_alias", ReadOnly = false, Destructive = true, Idempotent = false), Description("Remove a shared merchant alias after user confirmation.")]
    public static Task<JsonElement?> RemoveAlias(IHttpClientFactory clients, Guid merchantId, Guid aliasId, CancellationToken ct = default) => Send(clients, HttpMethod.Delete, $"staff/merchants/{merchantId:D}/aliases/{aliasId:D}", null, ct);

    [McpServerTool(Name = "staff_preview_merchant_merge", ReadOnly = true, Destructive = false, Idempotent = true), Description("Preview affected records and versions before merging shared merchants.")]
    public static Task<JsonElement?> PreviewMerge(IHttpClientFactory clients, Guid sourceId, Guid targetId, CancellationToken ct = default) => Send(clients, HttpMethod.Get, $"staff/merchants/{sourceId:D}/merge-preview?targetId={targetId:D}", null, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "staff_merge_merchants", ReadOnly = false, Destructive = true, Idempotent = false), Description("Merge shared merchants after reviewing the merge preview and obtaining explicit confirmation. Include the exact versions and impact counts from that preview.")]
    public static Task<JsonElement?> Merge(IHttpClientFactory clients, Guid sourceId, MergeMerchantsRequest merge, CancellationToken ct = default) => Send(clients, HttpMethod.Post, $"staff/merchants/{sourceId:D}/merge", merge, ct);

    private static string Query(params (string Key, string? Value)[] values)
    {
        var items = values.Where(x => x.Value is not null).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}");
        var query = string.Join("&", items);
        return query.Length == 0 ? "" : "?" + query;
    }

    private static async Task<JsonElement?> Send(IHttpClientFactory clients, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var client = clients.CreateClient("api");
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }
}

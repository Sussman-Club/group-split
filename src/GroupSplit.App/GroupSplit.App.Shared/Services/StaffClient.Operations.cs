using System.Net.Http.Json;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

public partial interface IStaffClient
{
    Task<List<StaffPersonResponse>> TeamAsync();
    Task<DirectoryPage<StaffActivityResponse>> ActivityAsync(string? resource, Guid? id, int page);
    Task<List<SupportUserResponse>> UsersAsync(string search);
    Task<DirectoryPage<SupportUserResponse>> UserDirectoryAsync(string search, int page);
    Task<SupportUserResponse> UserAsync(Guid id);
    Task<List<SavedStaffViewResponse>> ViewsAsync(string resource);
    Task SaveViewAsync(SaveStaffViewRequest request);
    Task DeleteViewAsync(Guid id);
    Task<List<SupportMessageResponse>> MessagesAsync(Guid id);
    Task<List<SupportMessageResponse>> MessagesAsync(Guid id, int page);
    Task ReplyAsync(Guid id, CreateSupportMessageRequest request);
    Task BulkTicketsAsync(BulkTicketUpdateRequest request);
    Task<DirectoryPage<SupportTicketSummary>> TicketsAsync(string search, string status, string priority, bool needsReview, int page, string assignee, string sort);
    Task<DirectoryPage<SupportTicketSummary>> TicketsAsync(string search, string status, string priority, bool needsReview, int page, string assignee, string sort, string type);
    Task<DirectoryPage<MerchantResponse>> MerchantsAsync(string search, int page, string sort);
    Task<List<MerchantAliasResponse>> AliasesAsync(Guid id);
    Task AddAliasAsync(Guid id, string name);
    Task RemoveAliasAsync(Guid id, Guid aliasId);
    Task<MerchantMergePreview> MergePreviewAsync(Guid id, Guid targetId);
    Task<MerchantResponse> MergeAsync(Guid id, MergeMerchantsRequest request);
    Task SaveMerchantImageAsync(Guid? id, string name, byte[] png, Guid? expectedVersion);
}

public sealed partial class StaffClient
{
    public async Task<List<StaffPersonResponse>> TeamAsync() => await http.GetFromApiJsonAsync<List<StaffPersonResponse>>("staff/team") ?? [];
    public async Task<DirectoryPage<StaffActivityResponse>> ActivityAsync(string? resource, Guid? id, int page) =>
        (await http.GetFromApiJsonAsync<DirectoryPage<StaffActivityResponse>>($"staff/activity?resource={Escape(resource)}&page={page}" + (id is null ? "" : $"&resourceId={id}")))!;
    public async Task<DirectoryPage<SupportUserResponse>> UserDirectoryAsync(string search, int page) => (await http.GetFromApiJsonAsync<DirectoryPage<SupportUserResponse>>($"staff/users/directory?search={Escape(search)}&page={page}"))!;
    public async Task<List<SupportUserResponse>> UsersAsync(string search) => await http.GetFromApiJsonAsync<List<SupportUserResponse>>($"staff/users?search={Escape(search)}") ?? [];
    public async Task<SupportUserResponse> UserAsync(Guid id) => (await http.GetFromApiJsonAsync<SupportUserResponse>($"staff/users/{id}"))!;
    public async Task<List<SavedStaffViewResponse>> ViewsAsync(string resource) => await http.GetFromApiJsonAsync<List<SavedStaffViewResponse>>($"staff/views?resource={Escape(resource)}") ?? [];
    public Task SaveViewAsync(SaveStaffViewRequest request) => SendAsync(HttpMethod.Post, "staff/views", request);
    public Task DeleteViewAsync(Guid id) => SendAsync(HttpMethod.Delete, $"staff/views/{id}", null);
    public async Task<List<SupportMessageResponse>> MessagesAsync(Guid id) => await http.GetFromApiJsonAsync<List<SupportMessageResponse>>($"staff/tickets/{id}/messages") ?? [];
    public async Task<List<SupportMessageResponse>> MessagesAsync(Guid id, int page) => await http.GetFromApiJsonAsync<List<SupportMessageResponse>>($"staff/tickets/{id}/messages?page={page}") ?? [];
    public Task ReplyAsync(Guid id, CreateSupportMessageRequest request) => SendAsync(HttpMethod.Post, $"staff/tickets/{id}/reply", request);
    public Task BulkTicketsAsync(BulkTicketUpdateRequest request) => SendAsync(HttpMethod.Post, "staff/tickets/bulk", request);
    public async Task<DirectoryPage<SupportTicketSummary>> TicketsAsync(string search, string status, string priority, bool needsReview, int page, string assignee, string sort) =>
        (await http.GetFromApiJsonAsync<DirectoryPage<SupportTicketSummary>>($"staff/tickets?search={Escape(search)}&status={Escape(status)}&priority={Escape(priority)}&needsReview={needsReview.ToString().ToLowerInvariant()}&page={page}&assignee={Escape(assignee)}&sort={Escape(sort)}"))!;
    public async Task<DirectoryPage<SupportTicketSummary>> TicketsAsync(string search, string status, string priority, bool needsReview, int page, string assignee, string sort, string type) =>
        (await http.GetFromApiJsonAsync<DirectoryPage<SupportTicketSummary>>($"staff/tickets?search={Escape(search)}&status={Escape(status)}&priority={Escape(priority)}&needsReview={needsReview.ToString().ToLowerInvariant()}&page={page}&assignee={Escape(assignee)}&sort={Escape(sort)}&type={Escape(type)}"))!;
    public async Task<DirectoryPage<MerchantResponse>> MerchantsAsync(string search, int page, string sort) =>
        (await http.GetFromApiJsonAsync<DirectoryPage<MerchantResponse>>($"staff/merchants?search={Escape(search)}&page={page}&sort={Escape(sort)}"))!;
    public async Task<List<MerchantAliasResponse>> AliasesAsync(Guid id) => await http.GetFromApiJsonAsync<List<MerchantAliasResponse>>($"staff/merchants/{id}/aliases") ?? [];
    public Task AddAliasAsync(Guid id, string name) => SendAsync(HttpMethod.Post, $"staff/merchants/{id}/aliases", new CreateMerchantAliasRequest(name));
    public Task RemoveAliasAsync(Guid id, Guid aliasId) => SendAsync(HttpMethod.Delete, $"staff/merchants/{id}/aliases/{aliasId}", null);
    public async Task<MerchantMergePreview> MergePreviewAsync(Guid id, Guid targetId) =>
        (await http.GetFromApiJsonAsync<MerchantMergePreview>($"staff/merchants/{id}/merge-preview?targetId={targetId}"))!;
    public async Task<MerchantResponse> MergeAsync(Guid id, MergeMerchantsRequest request)
    {
        using var response = await http.PostAsJsonAsync($"staff/merchants/{id}/merge", request);
        await response.EnsureApiSuccessAsync();
        return (await response.Content.ReadFromJsonAsync<MerchantResponse>())!;
    }

    private static string Escape(string? value) => Uri.EscapeDataString(value ?? "");
    private async Task SendAsync(HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await http.SendAsync(request);
        await response.EnsureApiSuccessAsync();
    }
}

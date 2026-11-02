using System.Net.Http.Json;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

public partial interface IStaffClient
{
    Task<bool> HasAccessAsync();
    Task<DirectoryPage<MerchantResponse>> MerchantsAsync(string search, int page);
    Task<MerchantResponse> MerchantAsync(Guid id);
    Task SaveMerchantAsync(Guid? id, UpdateMerchantRequest request);
    Task SaveMerchantImageAsync(Guid? id, string name, byte[] png);
    Task<DirectoryPage<SupportTicketResponse>> TicketsAsync(string search, string status, string priority, bool needsReview, int page);
    Task<SupportTicketResponse> TicketAsync(Guid id);
    Task<SupportTicketResponse> SaveTicketAsync(Guid id, UpdateSupportTicketRequest request);
    Task<List<SupportNoteResponse>> NotesAsync(Guid id);
    Task AddNoteAsync(Guid id, string body);
}

public sealed partial class StaffClient(HttpClient http) : IStaffClient
{
    public async Task<bool> HasAccessAsync() =>
        (await http.GetFromApiJsonAsync<StaffAccessResponse>("staff/access"))?.IsStaff == true;
    public async Task<DirectoryPage<MerchantResponse>> MerchantsAsync(string search, int page) =>
        (await http.GetFromApiJsonAsync<DirectoryPage<MerchantResponse>>($"staff/merchants?search={Uri.EscapeDataString(search)}&page={page}"))!;
    public async Task<MerchantResponse> MerchantAsync(Guid id) =>
        (await http.GetFromApiJsonAsync<MerchantResponse>($"staff/merchants/{id}"))!;
    public async Task<SupportTicketResponse> TicketAsync(Guid id) =>
        (await http.GetFromApiJsonAsync<SupportTicketResponse>($"staff/tickets/{id}"))!;
    public async Task SaveMerchantAsync(Guid? id, UpdateMerchantRequest request)
    {
        using var response = id is null
            ? await http.PostAsJsonAsync("staff/merchants", new CreateMerchantRequest { Name = request.Name, LogoUrl = request.LogoUrl })
            : await http.PutAsJsonAsync($"staff/merchants/{id}", request);
        await response.EnsureApiSuccessAsync();
    }

    public Task SaveMerchantImageAsync(Guid? id, string name, byte[] png) => SaveMerchantImageAsync(id, name, png, null);

    public async Task SaveMerchantImageAsync(Guid? id, string name, byte[] png, Guid? expectedVersion)
    {
        using var form = new MultipartFormDataContent();
        if (id is not null) form.Add(new StringContent(id.Value.ToString()), "merchantId");
        form.Add(new StringContent(name), "name");
        if (expectedVersion is not null) form.Add(new StringContent(expectedVersion.Value.ToString()), "expectedVersion");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "merchant-logo.png");
        using var response = await http.PostAsync("staff/merchant-images", form);
        await response.EnsureApiSuccessAsync();
    }
    public async Task<DirectoryPage<SupportTicketResponse>> TicketsAsync(string search, string status, string priority, bool needsReview, int page) =>
        (await http.GetFromApiJsonAsync<DirectoryPage<SupportTicketResponse>>($"staff/tickets?search={Uri.EscapeDataString(search)}&status={Uri.EscapeDataString(status)}&priority={Uri.EscapeDataString(priority)}&needsReview={needsReview.ToString().ToLowerInvariant()}&page={page}"))!;
    public async Task<SupportTicketResponse> SaveTicketAsync(Guid id, UpdateSupportTicketRequest request)
    {
        using var response = await http.PutAsJsonAsync($"staff/tickets/{id}", request);
        await response.EnsureApiSuccessAsync();
        return (await response.Content.ReadFromJsonAsync<SupportTicketResponse>())!;
    }
    public async Task<List<SupportNoteResponse>> NotesAsync(Guid id) =>
        await http.GetFromApiJsonAsync<List<SupportNoteResponse>>($"staff/tickets/{id}/notes") ?? [];
    public async Task AddNoteAsync(Guid id, string body)
    {
        using var response = await http.PostAsJsonAsync($"staff/tickets/{id}/notes", new CreateSupportNoteRequest(body));
        await response.EnsureApiSuccessAsync();
    }
}

using System.Net.Http.Json;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

public interface ISupportReportsClient
{
    Task<List<SupportReportResponse>> ListAsync();
    Task<SupportReportResponse> CreateAsync(CreateSupportReportRequest report);
    Task<List<SupportMessageResponse>> MessagesAsync(Guid id);
    Task<List<SupportMessageResponse>> MessagesAsync(Guid id, int page);
    Task ReplyAsync(Guid id, CreateSupportMessageRequest request);
}

public sealed class SupportReportsClient(HttpClient http) : ISupportReportsClient
{
    public async Task<List<SupportMessageResponse>> MessagesAsync(Guid id, int page) => await http.GetFromApiJsonAsync<List<SupportMessageResponse>>($"support-reports/{id}/messages?page={page}") ?? [];
    public async Task<List<SupportReportResponse>> ListAsync() =>
        await http.GetFromApiJsonAsync<List<SupportReportResponse>>("support-reports") ?? [];

    public async Task<List<SupportMessageResponse>> MessagesAsync(Guid id) => await http.GetFromApiJsonAsync<List<SupportMessageResponse>>($"support-reports/{id}/messages") ?? [];
    public async Task ReplyAsync(Guid id, CreateSupportMessageRequest request)
    {
        using var response = await http.PostAsJsonAsync($"support-reports/{id}/messages", request);
        await response.EnsureApiSuccessAsync();
    }
    public async Task<SupportReportResponse> CreateAsync(CreateSupportReportRequest report)
    {
        using var response = await http.PostAsJsonAsync("support-reports", report);
        await response.EnsureApiSuccessAsync();
        return (await response.Content.ReadFromJsonAsync<SupportReportResponse>())!;
    }
}

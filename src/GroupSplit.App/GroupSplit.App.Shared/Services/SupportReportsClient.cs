using System.Net.Http.Json;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

public interface ISupportReportsClient
{
    Task<List<SupportReportResponse>> ListAsync();
    Task<SupportReportResponse> CreateAsync(CreateSupportReportRequest report);
}

public sealed class SupportReportsClient(HttpClient http) : ISupportReportsClient
{
    public async Task<List<SupportReportResponse>> ListAsync() =>
        await http.GetFromJsonAsync<List<SupportReportResponse>>("support-reports") ?? [];

    public async Task<SupportReportResponse> CreateAsync(CreateSupportReportRequest report)
    {
        using var response = await http.PostAsJsonAsync("support-reports", report);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SupportReportResponse>())!;
    }
}

using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.ApiClient;
using GroupSplit.App.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using System.Net.Http.Json;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Fetch API URL from server configuration endpoint
using var configHttpClient = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var configResponse = await configHttpClient.GetFromJsonAsync<ApiConfig>("/api/config");
var apiUrl = (configResponse?.ApiUrl ?? "http://localhost:5144").TrimEnd('/');

// Add HTTP client for API
builder.Services.AddHttpClient("ApiClient", client =>
{
    client.BaseAddress = new Uri(apiUrl);
});

builder.Services.AddScoped<IClient>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("ApiClient");
    return new Client(httpClient.BaseAddress!.AbsoluteUri, httpClient);
});

await builder.Build().RunAsync();

record ApiConfig(string ApiUrl);

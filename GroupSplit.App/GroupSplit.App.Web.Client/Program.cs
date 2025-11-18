using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.ApiClient;
using GroupSplit.App.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add HTTP client for API
builder.Services.AddScoped<IClient>(sp =>
{
    var httpClient = new HttpClient();
    return new Client("http://localhost:5144", httpClient);
});

await builder.Build().RunAsync();

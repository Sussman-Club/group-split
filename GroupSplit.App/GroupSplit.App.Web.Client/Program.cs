using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using GroupSplit.App.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add HTTP client for API
builder.Services.AddHttpClient<IWeatherClient, WeatherClient>("ApiClient", client =>
{
    var uriBuilder = new UriBuilder(builder.HostEnvironment.BaseAddress)
    {
        Path = "api/"
    };
    
    client.BaseAddress = uriBuilder.Uri;
});

await builder.Build().RunAsync();

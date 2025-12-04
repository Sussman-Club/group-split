using GroupSplit.App.Shared.Services;
using GroupSplit.App.Web.Client.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add shared services
builder.Services.AddSharedServices(ServiceLifetime.Singleton);

// Add Auth client
builder.Services.AddHttpClient<IAuthService, AuthService>(client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
});

// Add HTTP client for API
builder.Services.AddHttpClient<IWeatherClient, WeatherClient>("ApiClient", client =>
{
    var uriBuilder = new UriBuilder(builder.HostEnvironment.BaseAddress)
    {
        Path = "api/"
    };
    
    client.BaseAddress = uriBuilder.Uri;
});

builder.Services.AddHttpClient<IUsersClient, UsersClient>("UserClient", client =>
{
    var uriBuilder = new UriBuilder(builder.HostEnvironment.BaseAddress)
    {
        Path = "api/"
    };

    client.BaseAddress = uriBuilder.Uri;
});

builder.Services.AddHttpClient<IGroupsClient, GroupsClient>(client =>
{
    var uriBuilder = new UriBuilder(builder.HostEnvironment.BaseAddress)
    {
        Path = "api/"
    };
    
    client.BaseAddress = uriBuilder.Uri;
});

builder.Services.AddHttpClient<ITransactionsClient, TransactionsClient>(client =>
{
    var uriBuilder = new UriBuilder(builder.HostEnvironment.BaseAddress)
    {
        Path = "api/"
    };
    
    client.BaseAddress = uriBuilder.Uri;
});

builder.Services.AddHttpClient<IRulesClient, RulesClient>(client =>
{
    var uriBuilder = new UriBuilder(builder.HostEnvironment.BaseAddress)
    {
        Path = "api/"
    };
    
    client.BaseAddress = uriBuilder.Uri;
});

// Add authorization services
builder.Services.AddAuthorizationCore();

await builder.Build().RunAsync();

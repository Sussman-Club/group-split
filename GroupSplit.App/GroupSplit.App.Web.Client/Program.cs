using GroupSplit.App.Shared.Services;
using GroupSplit.App.Web.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

builder.Services.AddSingleton<IClientOptionsSetter, ClientOptionsSetter>();

// Add shared services
builder.Services.AddSharedServices(ServiceLifetime.Singleton);

// Add Auth client
builder.Services.AddHttpClient<IAuthService, AuthService>(client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
});

// Add authorization services
builder.Services.AddAuthorizationCore();
builder.Services.AddSingleton<UserClientAuthenticationStateProvider>();
builder.Services.AddSingleton<AuthenticationStateProvider>(ActivatorUtilities.GetServiceOrCreateInstance<UserClientAuthenticationStateProvider>);

await builder.Build().RunAsync();

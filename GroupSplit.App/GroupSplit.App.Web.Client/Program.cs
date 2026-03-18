using GroupSplit.App.Shared.Services;
using GroupSplit.App.Web.Client.Services;
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
builder.Services.AddScoped<IAuthService, AuthService>();

// Add authorization services
builder.Services.AddAuthorizationCore();

await builder.Build().RunAsync();

using GroupSplit.App.Shared.Services;
using GroupSplit.App.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add MudBlazor services
builder.Services.AddMudServices();

builder.Services.AddSingleton<IClientOptionsSetter, ClientOptionsSetter>();

// Add shared services
builder.Services.AddSharedServices(ServiceLifetime.Singleton);

// Add Auth client
builder.Services.AddScoped<IAuthService, AuthService>();

// Add authorization services
builder.Services.AddAuthorizationCore();

var host = builder.Build();
var js = host.Services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>();
var language = await Microsoft.JSInterop.JSRuntimeExtensions.InvokeAsync<string>(js, "gs.language.read");
var culture = System.Globalization.CultureInfo.GetCultureInfo(GroupSplit.Shared.AppLanguages.Resolve(language));
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
await host.RunAsync();

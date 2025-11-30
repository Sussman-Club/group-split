using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.App.Web;
using GroupSplit.App.Web.Components;
using GroupSplit.App.Web.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.ConfigureOptions());

builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorizationBuilder();

// Add shared services
builder.Services.AddSharedServices();

// Initialize render mode configuration
var renderModePreference = builder.Configuration.GetValue<RenderModePreference>("RenderMode");
RenderModeConfig.Initialize(renderModePreference);

// Add services to the container.
builder.Services.AddRazorComponents()
    .RegisterPersistentService<GroupsTracker>(RenderMode.InteractiveAuto)
    .RegisterPersistentService<UserTransactionsTracker>(RenderMode.InteractiveAuto)
    .AddRenderModeComponents();

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add the forwarder to make sending requests to the backend easier
builder.Services.AddHttpForwarderWithServiceDiscovery();

// Add HTTP client for API (server-side)
builder.Services.AddHttpClient<IWeatherClient, WeatherClient>(client =>
{
    client.BaseAddress = new Uri("https+http://api");
}).AddHttpMessageHandler(ActivatorUtilities.GetServiceOrCreateInstance<AuthDelegatingHandler>);

builder.Services.AddHttpClient<IGroupsClient, GroupsClient>(client =>
{
    client.BaseAddress = new Uri("https+http://api");
}).AddHttpMessageHandler(ActivatorUtilities.GetServiceOrCreateInstance<AuthDelegatingHandler>);

builder.Services.AddHttpClient<ITransactionsClient, TransactionsClient>(client =>
{
    client.BaseAddress = new Uri("https+http://api");
}).AddHttpMessageHandler(ActivatorUtilities.GetServiceOrCreateInstance<AuthDelegatingHandler>);

builder.Services.AddHttpClient<IIdentityClient, IdentityClient>(client =>
{
    client.BaseAddress = new Uri("https+http://api");
});

// NOTE: The BFF invokes AuthService from the client-side only.
// This registration exists only to satisfy DI requirements.
builder.Services.AddSingleton<IAuthService, AuthService>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapApiForwarder();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .MapRenderMode(app);

app.MapIdentity();

app.Run();
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.App.Web;
using GroupSplit.App.Web.Components;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddKeycloakOpenIdConnect(
        serviceName: "keycloak",
        realm: "group-split",
        options =>
        {
            options.ClientId = "web-app";
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.ResponseType = OpenIdConnectResponseType.Code;
            
            options.SaveTokens = true;
            options.UsePkce = true;
            
            // For development only - disable HTTPS metadata validation
            // In production, use explicit Authority configuration instead
            if (builder.Environment.IsDevelopment())
            {
                options.RequireHttpsMetadata = false;
            }
        });

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddAuthorizationBuilder();

builder.Services.AddSingleton<IClientOptionsSetter, ClientOptionsSetter>();

// Add shared services
builder.Services.AddSharedServices();

// Initialize render mode configuration
var renderModePreference = builder.Configuration.GetValue<RenderModePreference>("RenderMode");
RenderModeConfig.Initialize(renderModePreference);

// Add services to the container.
builder.Services.AddRazorComponents()
    .RegisterPersistentService<GroupsTracker>(RenderMode.InteractiveAuto)
    .RegisterPersistentService<TransactionsTracker>(RenderMode.InteractiveAuto)
    .RegisterPersistentService<UserTracker>(RenderMode.InteractiveAuto)
    .AddRenderModeComponents();

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add the forwarder to make sending requests to the backend easier
builder.Services.AddHttpForwarderWithServiceDiscovery();
builder.Services.AddScoped<TokenRefreshService>();


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
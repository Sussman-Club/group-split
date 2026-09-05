using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.App.Web;
using GroupSplit.App.Web.Components;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Every service on localhost shares one cookie jar regardless of port, and each
// abandoned sign-in leaves an OIDC correlation and nonce cookie behind for
// fifteen minutes, scoped to /signin-oidc. Kestrel's 32KB defaults make that
// callback answer 431 once enough pile up: the total-size limit over HTTP/1.1,
// and the per-field limit over HTTP/2, which is what a browser uses on TLS and
// where one Cookie header is one field. The ticket itself is held server-side,
// so the session cookie is not what fills the header.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestHeadersTotalSize = 128 * 1024;
    options.Limits.Http2.MaxRequestHeaderFieldSize = 128 * 1024;
});

builder.AddGroupSplitAuthentication();

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
builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapApiForwarder();

app.MapKeycloakForwarder();

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

// Only pages get the friendly 404. The two forwarders must answer with their
// real status: re-executing an API 401 at /not-found lands on a Blazor page
// carrying [Authorize], which challenges OpenID Connect and turns the 401 into
// a 302 to Keycloak that a fetch cannot follow.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api")
               && !context.Request.Path.StartsWithSegments("/idp"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseDefaultHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .MapRenderMode(app);

app.MapIdentity();

app.Run();
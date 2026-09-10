using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Settling;
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

// Before authentication: the cookie, antiforgery and OIDC handlers all protect their
// payloads with this ring, and it has to outlive the container they run in.
builder.AddWebKeyRing();

// And before it too: the sign-in ticket and the tokens beside it are kept here, so a
// deploy that replaces this container leaves everybody signed in.
builder.AddRedisDistributedCache("cache");

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
    // Marked persistent like the others and never registered, so the plan the prerender
    // read was read again by the interactive side on every page, for the nav badge.
    .RegisterPersistentService<SettleTracker>(RenderMode.InteractiveAuto)
    .RegisterPersistentService<UserTracker>(RenderMode.InteractiveAuto)
    .AddRenderModeComponents();

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add the forwarder to make sending requests to the backend easier
builder.Services.AddHttpForwarderWithServiceDiscovery();

// NOTE: The BFF invokes AuthService from the client-side only.
// This registration exists only to satisfy DI requirements.
builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapApiForwarder();

app.MapNativeApiForwarder();

app.MapKeycloakForwarder();

app.MapWebhookForwarder();

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

// Only pages get the friendly 404. The forwarders must answer with their real
// status: re-executing an API 401 at /not-found lands on a Blazor page carrying
// [Authorize], which challenges OpenID Connect and turns the 401 into a 302 to
// Keycloak that a fetch cannot follow -- and that a CLI cannot follow either,
// which is why /native is excluded here as well as /api.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api")
               && !context.Request.Path.StartsWithSegments("/native")
               && !context.Request.Path.StartsWithSegments("/idp")
               // A provider reads the status code and retries on anything but a 2xx, so a
               // friendly 404 page in place of the API's answer would have it retrying a
               // webhook that was in fact refused on purpose.
               && !context.Request.Path.StartsWithSegments(WebAppExtensions.WebhookPrefix),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
// The webhook prefix is exempt, and not because of the dev tunnel -- that points at the
// HTTPS endpoint and needs nothing here. It is because of the shape a deployment has:
// TLS stops at the proxy and the hop to this app is plain HTTP. Webhooks survive that
// today only because a deployed app is given no HTTPS port, so the redirection is inert;
// give it one and every webhook becomes a 307 the provider records as a failure. This
// makes that an intention rather than an accident.
app.UseDefaultHttpsRedirection(WebAppExtensions.WebhookPrefix);

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .MapRenderMode(app);

app.MapIdentity();

app.Run();
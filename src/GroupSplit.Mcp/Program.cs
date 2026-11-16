using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var issuer = builder.Configuration["Mcp:Issuer"] ?? throw new InvalidOperationException("Mcp:Issuer is required.");
var resource = builder.Configuration["Mcp:Resource"] ?? throw new InvalidOperationException("Mcp:Resource is required.");
if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps ||
    !string.IsNullOrEmpty(issuerUri.UserInfo) || !string.IsNullOrEmpty(issuerUri.Query) || !string.IsNullOrEmpty(issuerUri.Fragment) ||
    !Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || resourceUri.Scheme != Uri.UriSchemeHttps ||
    !string.IsNullOrEmpty(resourceUri.UserInfo) || resourceUri.AbsolutePath != "/mcp" || !string.IsNullOrEmpty(resourceUri.Query) || !string.IsNullOrEmpty(resourceUri.Fragment))
{
    throw new InvalidOperationException("Issuer and resource must be HTTPS URLs; resource must end in /mcp.");
}
var metadata = $"{resourceUri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource/mcp";
// Health probes use localhost on a separate management port. Public requests still
// have to use the configured resource host, enforced by the middleware below.
builder.Configuration["AllowedHosts"] = $"{resourceUri.Host};localhost";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = issuer;
    options.Audience = resource;
    options.TokenValidationParameters.IgnoreTrailingSlashWhenValidatingAudience = false;
    options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    options.Events = new JwtBearerEvents
    {
        OnChallenge = context =>
        {
            context.HandleResponse();
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = $"Bearer scope=\"mcp:read\", resource_metadata=\"{metadata}\"";
            return Task.CompletedTask;
        },
        OnForbidden = context =>
        {
            context.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"mcp:read\", resource_metadata=\"{metadata}\"";
            return Task.CompletedTask;
        }
    };
}).AddMcp(options => options.ResourceMetadata = new ProtectedResourceMetadata
{
    Resource = resource,
    AuthorizationServers = { issuer },
    ScopesSupported = ["mcp:read", "mcp:write"]
});
builder.Services.AddAuthorizationBuilder().AddPolicy("mcp:read", policy => policy.RequireAuthenticatedUser()
    .RequireAssertion(context => context.User.FindAll("scope").SelectMany(c => c.Value.Split(' ')).Contains("mcp:read")))
    .AddPolicy("mcp:write", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => context.User.FindAll("scope").SelectMany(c => c.Value.Split(' ')).Contains("mcp:write")));
var exchangeClientId = builder.Configuration["Mcp:ExchangeClientId"] ?? throw new InvalidOperationException("Mcp:ExchangeClientId is required.");
var exchangeSecret = builder.Configuration["Mcp:ExchangeClientSecret"] ?? throw new InvalidOperationException("Mcp:ExchangeClientSecret is required.");
if (string.IsNullOrWhiteSpace(exchangeClientId) || string.IsNullOrWhiteSpace(exchangeSecret))
{
    throw new InvalidOperationException("A confidential token exchange client is required.");
}
builder.Services.AddGroupSplitApiClients(builder.Configuration);
builder.Services.AddGroupSplitMcpTools();
var app = builder.Build();
app.MapDefaultEndpoints();
app.Use(async (context, next) =>
{
    var managementPort = builder.Configuration.GetValue<int?>("HealthChecks:Port");
    var isManagementProbe = managementPort is not null && context.Connection.LocalPort == managementPort &&
        (context.Request.Path == "/health" || context.Request.Path == "/alive");
    if (!isManagementProbe && !string.Equals(context.Request.Host.Host, resourceUri.Host, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 400;
        return;
    }
    if (context.Request.Headers.ContainsKey("Origin"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization("mcp:read");
app.Run();

public partial class Program;
public sealed class McpHostMarker;

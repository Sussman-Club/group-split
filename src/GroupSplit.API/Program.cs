using GroupSplit.API.Endpoints;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Middleware;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.Data.PostgreSQL;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.AddServiceDefaults();

// Configure auth
builder.Services.AddAuthentication()
    .AddKeycloakJwtBearer(
        serviceName: "keycloak",
        realm: "group-split",
        options =>
        {
            options.Audience = "api";

            if (builder.Environment.IsDevelopment())
            {
                options.RequireHttpsMetadata = false;
            }
            else
            {
                // Service discovery cannot satisfy RequireHttpsMetadata.
                options.Authority = builder.Configuration["Keycloak:Authority"]
                    ?? throw new InvalidOperationException(
                        "Keycloak:Authority must be configured outside of development.");

                // Defaults to the strictest setting the authority can support: an https
                // authority gets metadata validation, an http one cannot have it. So putting
                // TLS in front of Keycloak turns this on by itself.
                options.RequireHttpsMetadata =
                    builder.Configuration.GetValue<bool?>("Keycloak:RequireHttpsMetadata")
                    ?? options.Authority.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            }
        });

builder.Services.AddAuthorizationBuilder();

builder.AddPostgreSqlAppDbContext("db");
builder.Services.AddDomainServices();

// The Data Protection key ring the bank access tokens are encrypted with, in the app
// database and itself encrypted with a certificate the deployment holds as a secret.
builder.AddBankKeyRing();

// The two pieces of banking that read configuration, which only a real host has. Validated on
// start, because a public origin no provider would accept is a deployment mistake and the
// alternative is finding out from the first person who tries to link a bank.
builder.Services.AddOptions<BankingOptions>()
    .BindConfiguration(BankingOptions.SectionName)
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<BankingOptions>, BankingOptionsValidator>();

// Only when this deployment has Plaid credentials. Without them the API starts as usual and
// the bank features say bank sync is off, which is the honest answer.
builder.Services.AddPlaidConnector(builder.Configuration);

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApiDocuments();

builder.Services.AddApiValidation();
builder.Services.AddApiErrorHandling();

var app = builder.Build();

// Before the server is listening, not from a hosted service: the one that starts Kestrel is
// registered while the builder is constructed, so anything added later starts after it and
// would leave a window where requests are served by a host about to be refused.
await app.VerifyBankKeyRing();

// Before anything else, so that every failure below it leaves as problem details.
app.UseApiErrorHandling();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Exempt for the same reason the web app's copy is: the forwarder in front of this
// prefers the HTTPS endpoint, but a fallback to the plain one must not turn a webhook
// into a redirect the provider will record as a failure.
//
// What this leans on, said out loud because it is an assumption and not a fact of the
// code: TLS reaches the proxy and the hop behind it is trusted. Nothing about a webhook is
// secret enough to matter on that hop -- item and account ids, and a code -- and the
// provider's signature over the body is what authenticates it, not the transport. Expose
// this application's plain port to anywhere untrusted and that stops being true.
app.UseDefaultHttpsRedirection([WebhooksApi.Prefix]);

app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapDomainApi();

app.Run();

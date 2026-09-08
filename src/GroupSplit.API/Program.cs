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

builder.Services.AddValidation();
builder.Services.AddApiErrorHandling();

var app = builder.Build();

// Before anything else, so that every failure below it leaves as problem details.
app.UseApiErrorHandling();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultHttpsRedirection();

app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapDomainApi();

app.Run();

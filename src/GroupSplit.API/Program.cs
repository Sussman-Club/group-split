using GroupSplit.API.Endpoints;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;

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
            }
        });

builder.Services.AddAuthorizationBuilder();

builder.Services.AddHttpContextAccessor();

builder.AddPostgreSqlAppDbContext("db");
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IDebtCalculationService, DebtCalculationService>();
builder.Services.AddScoped<IGroupService, GroupService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddRuleVersionServices();
builder.Services.AddScoped<IRuleService, RuleService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApiDocuments();

builder.Services.AddValidation();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGroupApi();
app.MapUserApi();
app.MapTransaction();
app.MapRulesApi();

app.Run();
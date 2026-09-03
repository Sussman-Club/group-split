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
            
            // For development only - disable HTTPS metadata validation
            // In production, use explicit Authority configuration instead
            if (builder.Environment.IsDevelopment())
            {
                options.RequireHttpsMetadata = false;
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

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast")
    .WithTags("weather")
    .RequireAuthorization();

app.MapGroupApi();
app.MapUserApi();
app.MapTransaction();
app.MapRulesApi();

app.Run();
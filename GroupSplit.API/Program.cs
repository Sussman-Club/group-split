using GroupSplit.API.Extensions;
using GroupSplit.API.Users;
using GroupSplit.Identity;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NSwag.Generation.Processors;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure auth
builder.Services.AddAuthentication().AddBearerToken(IdentityConstants.BearerScheme);
builder.Services.AddAuthorizationBuilder();

// Configure identity
builder.Services.AddIdentityCore<User>()
    .AddEntityFrameworkStores<AppIdentityContext>()
    .AddApiEndpoints();

builder.Services.AddDbContext<AppIdentityContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("identity"));
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

// Add NSwag for build-time OpenAPI spec generation
builder.Services.AddOpenApiDocument(options =>
{
    options.OperationProcessors.Add(new OperationProcessor(ctx =>
        !ctx.OperationDescription.Path.StartsWith("/users", StringComparison.OrdinalIgnoreCase)));
});
builder.Services.AddOpenApiDocument(options =>
{
    options.DocumentName = "identity";
    options.OperationProcessors.Insert(0, new OperationProcessor(ctx =>
        ctx.OperationDescription.Path.StartsWith("/users", StringComparison.OrdinalIgnoreCase)));
});

// Add CORS
builder.Services.AddCors(options =>
{
    var urls = new List<string>();

    // Try to get the web service URL from Aspire configuration
    var httpUrl = builder.Configuration["services:web:http:0"];
    var httpsUrl = builder.Configuration["services:web:https:0"];

    if (!string.IsNullOrEmpty(httpUrl))
        urls.Add(httpUrl);

    if (!string.IsNullOrEmpty(httpsUrl))
        urls.Add(httpsUrl);

    // Fallback to local URLs if running outside Aspire
    if (urls.Count == 0)
        urls.AddRange(["http://localhost:5041", "https://localhost:7287"]);

    options.AddPolicy("AllowBlazorClient", policy =>
    {
        policy.WithOrigins([.. urls])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// CORS must come before UseHttpsRedirection to add headers to redirect responses
app.UseCors("AllowBlazorClient");

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapUsers();

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
    .RequireAuthorization();

app.Run();
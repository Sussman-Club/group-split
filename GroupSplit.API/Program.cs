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
    options.OperationProcessors.Add(new OperationProcessor(ctx =>
        ctx.OperationDescription.Path.StartsWith("/users", StringComparison.OrdinalIgnoreCase)));
    
    options.DocumentProcessors.Add(new ActionDocumentProcessor(ctx =>
    {
        var document = ctx.Document;
        
        foreach (var (schemaName, schema) in document.Components.Schemas)
        {
            // Try to see if this schema was created from a .NET type
            var type = schema.ExtensionData != null &&
                       schema.ExtensionData.TryGetValue("x-type", out var tObj) &&
                       tObj is Type t &&
                       t.FullName?.StartsWith("GroupSplit.Shared") is true
                ? t
                : null;

            // If you don't have x-type extensions, you may instead track types
            // via a SchemaProcessor (see below) and mark them in ExtensionData.

            if (type is not null)
            {
                document.Components.Schemas.Remove(schemaName);
            }
        }
    }));
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
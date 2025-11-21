using GroupSplit.API.Extensions;
using GroupSplit.API.Users;
using GroupSplit.Identity;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NSwag.Generation.Processors;
using Scalar.AspNetCore;
using GroupSplit.Data;
using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;

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

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("db"));
});

builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddScoped<IGroupService, GroupService>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

// Add NSwag for build-time OpenAPI spec generation
builder.Services.AddOpenApiDocument(options =>
{
    options.OperationProcessors.Add(new OperationProcessor(ctx =>
        !ctx.OperationDescription.Path.StartsWith("/identity", StringComparison.OrdinalIgnoreCase)));
    
    options.OperationProcessors.Add(new OperationProcessor(ctx =>
    {
        if (ctx.OperationDescription.Operation.Tags.Count == 0)
        {
            ctx.OperationDescription.Operation.Tags.Add("api");
            // We add a default tag so we do not get a weird IClient in the generated files.
        }

        return true;
    }));
});
builder.Services.AddOpenApiDocument(options =>
{
    options.DocumentName = "identity";
    options.OperationProcessors.Insert(0, new OperationProcessor(ctx =>
        ctx.OperationDescription.Path.StartsWith("/identity", StringComparison.OrdinalIgnoreCase)));
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapIdentity();

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
    .WithTags("weather");

app.MapGroupApi();
app.MapUserApi();

app.Run();
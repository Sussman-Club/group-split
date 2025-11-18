using GroupSplit.App.Web.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.ApiClient;
using GroupSplit.App.Web.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// Add device-specific services used by the GroupSplit.App.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add HTTP client for API (server-side)
builder.Services.AddScoped<IClient>(sp =>
{
    var httpClient = new HttpClient();
    return new Client("http://localhost:5144", httpClient);
});

var app = builder.Build();

app.MapDefaultEndpoints();

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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(GroupSplit.App.Shared._Imports).Assembly,
        typeof(GroupSplit.App.Web.Client._Imports).Assembly);

app.Run();

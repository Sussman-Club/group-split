using GroupSplit.App.Services;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace GroupSplit.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Add shared services
        builder.Services.AddSharedServices();

        // Add device-specific services used by the GroupSplit.App.Shared project
        builder.Services.AddSingleton<IFormFactor, FormFactor>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<IAuthService>(ActivatorUtilities.GetServiceOrCreateInstance<AuthService>);

        // Add Auth client
        builder.Services.AddHttpClient<IIdentityClient, IdentityClient>(client =>
        {
            client.BaseAddress = new Uri("https+http://api");
        });

        // Add HttpClient for API calls
        builder.Services.AddHttpClient<IWeatherClient, WeatherClient>(client =>
        {
            client.BaseAddress = new Uri("https+http://api");
        }).AddHttpMessageHandler(ActivatorUtilities.GetServiceOrCreateInstance<AuthDelegatingHandler>);

        builder.Services.AddMauiBlazorWebView();

        // Add MudBlazor services
        builder.Services.AddMudServices();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

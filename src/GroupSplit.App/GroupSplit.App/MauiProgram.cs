using GroupSplit.App.Services;
using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

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
        builder.Services.AddScoped<GroupSplit.App.Shared.Services.Banking.IBankLinkLauncher, MobileBankLinkLauncher>();
        builder.Services.AddSharedServices(ServiceLifetime.Singleton);

        // Add device-specific services used by the GroupSplit.App.Shared project
        var metadata = typeof(MauiProgram).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value ?? "");
        var options = MobileOptions.Create(metadata["MobileApiUrl"], metadata["MobileAuthority"],
#if DEBUG
            true
#else
            false
#endif
        );
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IMobileBrowser, MobileBrowser>();
        builder.Services.AddSingleton<IMobileTokenStore, MobileTokenStore>();
        builder.Services.AddSingleton(sp => new MobileSession(options, new HttpClient(),
            sp.GetRequiredService<IMobileBrowser>(), sp.GetRequiredService<IMobileTokenStore>()));
        builder.Services.AddSingleton<IClientOptionsSetter, ClientOptionsSetter>();
        builder.Services.AddScoped<IAuthService, AuthService>();

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

using GroupSplit.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace GroupSplit.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices();
        builder.Services.AddTransient<MainPage>();
#if ANDROID
        builder.Services.AddSingleton<Shared.Services.IReceiptCamera, NativeReceiptCamera>();
#endif

#if DEBUG
        const bool development = true;
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#else
        const bool development = false;
#endif

        builder.Services.AddNativeServices(NativeOptions.FromAssembly(typeof(MauiProgram).Assembly, development));
        return builder.Build();
    }
}

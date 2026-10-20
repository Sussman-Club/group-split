using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace GroupSplit.App.Services;

public static class NativeServiceExtensions
{
    public static IServiceCollection AddNativeServices(this IServiceCollection services, NativeOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IWebAuthenticator>(_ => WebAuthenticator.Default);
        services.AddSingleton<ISecureStorage>(_ => SecureStorage.Default);
        services.AddSingleton<INativeBrowser, NativeBrowser>();
        services.AddSingleton<INativeTokenStore, NativeTokenStore>();
        services.AddSingleton<NativeSession>();
        services.AddHttpClient(NativeSession.IdentityClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddTransient<AuthDelegatingHandler>();
        services.AddSingleton<IClientOptionsSetter, ClientOptionsSetter>();

        // BlazorWebView owns the scope for its components, user state and navigation.
        // Only the secure token session is shared by every native window.
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IBankLinkLauncher, NativeBankLinkLauncher>();
        services.AddSharedServices();
        return services;
    }
}

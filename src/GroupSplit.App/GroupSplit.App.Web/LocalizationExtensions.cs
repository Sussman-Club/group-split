using GroupSplit.Shared;
using Microsoft.AspNetCore.Localization;

namespace GroupSplit.App.Web;

public static class LocalizationExtensions
{
    public static IServiceCollection AddWebLocalization(this IServiceCollection services)
    {
        services.AddLocalization();
        services.Configure<RequestLocalizationOptions>(options =>
        {
            var languages = AppLanguages.Supported.ToArray();
            options.SetDefaultCulture("en").AddSupportedCultures(languages).AddSupportedUICultures(languages);
            options.RequestCultureProviders =
            [
                new AccountRequestCultureProvider(),
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider()
            ];
        });
        return services;
    }
}

using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Localization;

namespace GroupSplit.App.Web;

/// <summary>Reads the account before rendering, so SSR and the interactive circuit agree.</summary>
public sealed class AccountRequestCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated is not true
            || context.Request.GetTypedHeaders().Accept?.Any(h => h.MediaType.Value == "text/html") is not true)
            return null;

        try
        {
            var user = await context.RequestServices.GetRequiredService<IUsersClient>()
                .GetCurrentUserAsync(context.RequestAborted);
            if (AppLanguages.IsSupported(user.PreferredLanguage))
                return new ProviderCultureResult(user.PreferredLanguage!);
        }
        catch (Exception ex) when (ApiErrors.IsApiFailure(ex))
        {
            // The account loader presents API failures. Localization can still use the cookie.
        }
        return null;
    }
}

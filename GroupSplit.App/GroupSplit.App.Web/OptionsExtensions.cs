using Microsoft.AspNetCore.Authentication.Cookies;

namespace GroupSplit.App.Web;

public static class OptionsExtensions
{
    extension(CookieAuthenticationOptions options)
    {
        public void ConfigureOptions()
        {
            // Prevent redirect on 401
            options.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                var returnUrl = Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString.ToString());
                var loginUrl = $"/login?returnUrl={returnUrl}";
                ctx.Response.Redirect(loginUrl);
                return Task.CompletedTask;

            };

            // Prevent redirect on 403
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        }
    }
}
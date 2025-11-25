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
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
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
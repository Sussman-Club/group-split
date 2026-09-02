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

                // Normalizing the return URL and redirecting to login page
                var path = ctx.Request.Path;
                var returnUrl = Uri.EscapeDataString(path + ctx.Request.QueryString.ToString());
                var returnQuery = path.Value == "/" || string.IsNullOrEmpty(path.Value) ? "" : $"?returnUrl={returnUrl}";
                ctx.Response.Redirect($"/login{returnQuery}");

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
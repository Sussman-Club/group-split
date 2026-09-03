using System.Net.Http.Headers;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

#pragma warning disable ASP0018

namespace GroupSplit.App.Web;

public static class WebAppExtensions
{
    /// <summary>
    /// Path Keycloak is forwarded from. Matches the AppHost's
    /// <c>KeycloakDeploymentExtensions.RelativePath</c>, which is what Keycloak is told
    /// to serve at.
    /// </summary>
    private const string AuthPrefix = "/auth";

    extension(IEndpointRouteBuilder app)
    {
        public RouteGroupBuilder MapApiForwarder()
        {
            var group = app.MapGroup("/api");
            
            group.RequireAuthorization();

            group.MapForwarder("{*path}","https+http://api", new ForwarderRequestConfig(), b =>
            {
                b.AddRequestTransform(async requestTransformContext =>
                {
                    if (requestTransformContext.Path.StartsWithSegments("/api", out var other))
                    {
                        requestTransformContext.Path = other;
                    }

                    var tokenRefreshService = requestTransformContext.HttpContext.RequestServices.GetRequiredService<TokenRefreshService>();
                    var accessToken = await tokenRefreshService.GetValidAccessTokenAsync(requestTransformContext.HttpContext, requestTransformContext.CancellationToken);

                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        await requestTransformContext.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }

                    requestTransformContext.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                });
            });
            
            return group;
        }

        /// <summary>
        /// Carries Keycloak on this app's own origin, so a deployment publishes one port
        /// rather than one per service -- the same arrangement <c>MapApiForwarder</c> gives
        /// the API. The browser still drives the OIDC redirects itself; they just stay
        /// same-origin.
        /// <para>
        /// A straight passthrough: the prefix is deliberately left on the forwarded path,
        /// because Keycloak is configured to serve at it (<c>KC_HTTP_RELATIVE_PATH</c>) and
        /// scopes its session cookies to the paths it generates. Stripping the prefix here
        /// would mint cookies the browser never sends back, and sign-in would loop silently.
        /// </para>
        /// <para>
        /// Anonymous by necessity: these are the pages a challenge sends the browser to, so
        /// requiring authentication would be a redirect loop. Antiforgery is off because
        /// Keycloak's own login forms carry its tokens, not this app's.
        /// </para>
        /// <para>
        /// Mapped in every environment. Locally the authority points straight at Keycloak's
        /// own endpoint, which leaves this route unused rather than wrong, and that is
        /// cheaper than branching the pipeline on the environment.
        /// </para>
        /// </summary>
        public IEndpointConventionBuilder MapAuthForwarder()
            => app.MapForwarder($"{AuthPrefix}/{{*path}}", "http://keycloak")
                .AllowAnonymous()
                .DisableAntiforgery();
    }
}
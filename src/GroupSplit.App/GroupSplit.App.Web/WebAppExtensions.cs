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
    /// to serve at. Distinct from the <c>/auth</c> group <c>MapIdentity</c> owns.
    /// </summary>
    private const string KeycloakPrefix = "/idp";

    /// <summary>
    /// Where token-bearing clients reach the API. A separate top-level segment rather than
    /// a path under <c>/api</c>, which the forwarder there would swallow and proxy on as if
    /// it were an API route.
    /// </summary>
    private const string NativeApiPrefix = "/native/api";

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
        /// The door for clients that hold a token rather than a session: the CLI, and the
        /// MAUI app once it can be pointed at a deployment.
        /// <para>
        /// <c>MapApiForwarder</c> above cannot serve them. It authenticates with this app's
        /// cookie and then <em>replaces</em> the Authorization header with the token from
        /// that session, so a caller arriving with its own bearer token is challenged and,
        /// if it somehow got past, would have its token thrown away. That is right for the
        /// browser, which must never hold a token, and wrong for everything else.
        /// </para>
        /// <para>
        /// Anonymous, and deliberately so: every endpoint group in the API calls
        /// <c>RequireAuthorization</c>, and its health checks answer only on a management
        /// port that is never published. So there is nothing behind this to reach without a
        /// token, and authenticating here as well would mean validating the same token twice
        /// and maintaining a second scheme in this app to do it. The header is passed
        /// through untouched and the API decides, which is what it already does for every
        /// request the browser sends through the other route.
        /// </para>
        /// <para>
        /// Antiforgery is off for the same reason it is off for Keycloak: these callers have
        /// no session and no antiforgery token, and the API is not cookie-authenticated, so
        /// there is no ambient credential for a cross-site request to abuse.
        /// </para>
        /// <para>
        /// The prefix is stripped, unlike the Keycloak route, because the API serves at the
        /// root and knows nothing of the path this app publishes it under.
        /// </para>
        /// </summary>
        public RouteGroupBuilder MapNativeApiForwarder()
        {
            var group = app.MapGroup(NativeApiPrefix);

            group.MapForwarder("{*path}", "https+http://api", new ForwarderRequestConfig(), builder =>
            {
                builder.AddRequestTransform(context =>
                {
                    if (context.Path.StartsWithSegments(NativeApiPrefix, out var rest))
                    {
                        context.Path = rest;
                    }

                    return ValueTask.CompletedTask;
                });
            });

            group.AllowAnonymous().DisableAntiforgery();

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
        public IEndpointConventionBuilder MapKeycloakForwarder()
            => app.MapForwarder($"{KeycloakPrefix}/{{*path}}", "http://keycloak")
                .AllowAnonymous()
                .DisableAntiforgery();
    }
}
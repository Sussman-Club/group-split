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
    /// Path bank providers send webhooks to. Matches the API's own <c>WebhooksApi</c> route,
    /// and is deliberately left on the forwarded path so the API still sees which provider
    /// is calling.
    /// </summary>
    public const string WebhookPrefix = "/webhooks";

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
        public IEndpointConventionBuilder MapKeycloakForwarder()
            => app.MapForwarder($"{KeycloakPrefix}/{{*path}}", "http://keycloak")
                .AllowAnonymous()
                .DisableAntiforgery();

        /// <summary>
        /// Carries a bank provider's webhooks through to the API.
        /// <para>
        /// The <c>/api</c> forwarder requires authentication, and a provider has no account
        /// here and no token, so its calls would be answered with a 401 and the app would
        /// simply never hear that anything had changed. This is the same passthrough
        /// without that requirement.
        /// </para>
        /// <para>
        /// Anonymous is not unguarded. The API verifies the provider's signature over the
        /// exact bytes forwarded here before it reads any of them, so what stands in for a
        /// token is cryptographic rather than absent -- and this hop must not disturb those
        /// bytes, which is why it is a passthrough and not a transform.
        /// </para>
        /// <para>
        /// Antiforgery is off for the same reason Keycloak's is: the caller is not a browser
        /// carrying this app's tokens.
        /// </para>
        /// </summary>
        public IEndpointConventionBuilder MapWebhookForwarder()
            => app.MapForwarder($"{WebhookPrefix}/{{*path}}", "https+http://api")
                .AllowAnonymous()
                .DisableAntiforgery();
    }
}
using Aspire.Hosting.DevTunnels;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.AppHost.Extensions;

/// <summary>
/// A dev tunnel that lets a bank provider reach a development machine, so the webhook half
/// of bank sync can be exercised without deploying.
/// </summary>
public static class WebhookTunnelExtensions
{
    /// <summary>
    /// The switch that adds the tunnel, declared as <c>false</c> in
    /// <c>appsettings.Development.json</c> so the knob is visible in the repo rather than
    /// only in whoever's user secrets already have it.
    /// </summary>
    /// <remarks>
    /// A configuration key and not an Aspire parameter, because parameters are what differs
    /// between deployments and this differs between developers: it exists only in run mode,
    /// and a published stack has a real origin and no use for a tunnel.
    /// </remarks>
    public const string EnabledKey = "WebhookTunnel";

    extension<T>(IResourceBuilder<T> api) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// Publishes the web app through a dev tunnel and tells the API to hand that address
        /// to bank providers. Does nothing unless <see cref="EnabledKey"/> is true.
        /// </summary>
        /// <param name="web">
        /// The resource the tunnel fronts. The web app, because that is the shape a
        /// deployment has: the provider calls the public origin, and the web app's
        /// <c>/webhooks</c> forwarder carries the call through to the API unchanged. Tunnel
        /// the API directly and the forwarder -- the one hop a webhook makes that nothing
        /// else does -- is the one piece a local run would not exercise.
        /// </param>
        /// <remarks>
        /// Optional because it is not free: it needs the <c>devtunnel</c> CLI and a signed-in
        /// account, and it publishes a port of this machine to the internet for as long as the
        /// run lasts. Nobody who is not working on bank sync should have to have either.
        /// <para>
        /// Anonymous access is what a provider needs: it has no account here and no token, and
        /// what stands in for one is its signature over the bytes it sent, which the API
        /// checks before reading them. It does mean the whole local web app answers on that
        /// address, which is the other half of why this is off by default.
        /// </para>
        /// <para>
        /// On, it is a resource of its own rather than something folded into the web app, so
        /// the dashboard names it, shows the address it was given and says whether it is up.
        /// </para>
        /// </remarks>
        public IResourceBuilder<T> WithWebhookTunnel<TFront>(IResourceBuilder<TFront> web)
            where TFront : IResourceWithEndpoints
        {
            var builder = api.ApplicationBuilder;

            if (!builder.Configuration.GetValue(EnabledKey, false))
                return api;

            // Named for what it is for rather than for what it fronts, the way the webhook
            // route itself is: lowercase and hyphenated, which is what Aspire allows in a
            // resource name and what every multi-word name in this AppHost already uses. The
            // labels are for the developer's own tunnel list, where this is one of several.
            var tunnel = builder
                .AddDevTunnel("webhook-tunnel", options: new DevTunnelOptions
                {
                    Description = "Bank provider webhooks to a development machine",
                    Labels = ["groupsplit", "webhooks"]
                })
                .WithReference(web.GetEndpoint("http"), allowAnonymous: true);

            // The same setting a deployment fills from its public origin, so nothing below
            // the AppHost knows a tunnel is involved.
            return api.WithEnvironment("Banking__PublicOrigin", tunnel.GetEndpoint(web, "http"));
        }
    }
}

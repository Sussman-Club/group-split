using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace GroupSplit.AppHost.Extensions;

public static class KeycloakDeploymentExtensions
{
    /// <summary>
    /// Path Keycloak is served under once deployed.
    /// <para>
    /// Nothing publishes Keycloak's own port: the web app forwards this prefix to it,
    /// so the stack exposes one origin. Keycloak has to serve at the prefix rather
    /// than have the forwarder strip it, because it scopes its session cookies to
    /// the paths it generates -- stripping would mint <c>Path=/realms/...</c> cookies
    /// that a browser never sends back to <c>/idp/realms/...</c>, and sign-in would
    /// loop with no error.
    /// </para>
    /// <para>
    /// Deliberately not <c>/auth</c>: the web app's own <c>MapIdentity</c> group owns
    /// that prefix for the endpoints that start and end a session. Route precedence
    /// would keep both working, but the two would be indistinguishable by path.
    /// </para>
    /// </summary>
    public const string RelativePath = "/idp";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Prepares Keycloak to run outside local development. The publish-mode counterpart
        /// of <see cref="KeycloakDevelopmentExtensions.AsDevelopmentKeycloak"/>.
        /// <para>
        /// <c>WithRealmImport</c> relies on development-time container file
        /// injection that <c>aspire publish</c> cannot express: it degrades to a
        /// bind mount pointing at a host path that will not exist on the target
        /// machine. The realm and theme are shipped as Compose configs instead,
        /// which keeps the stock upstream image and avoids pushing a derived one.
        /// </para>
        /// </summary>
        /// <param name="hostname">
        /// The web app's public origin. Keycloak shares it, under <see cref="RelativePath"/>.
        /// </param>
        public IResourceBuilder<KeycloakResource> AsDeployedKeycloak(
            IResourceBuilder<ParameterResource> hostname)
        {
            return keycloak
                .WithFiles("Assets/keycloak/realms.json", "/opt/keycloak/data/import/realms.json")
                .WithFiles("Assets/keycloak/themes", "/opt/keycloak/themes/group-split")
                // Production mode serves HTTPS only unless plain HTTP is opted into. TLS is
                // terminated upstream, so Keycloak speaks plain HTTP and learns the original
                // scheme and host from the forwarded headers the proxy sets.
                .WithEnvironment("KC_HTTP_ENABLED", "true")
                .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
                .WithEnvironment("KC_HOSTNAME", ReferenceExpression.Create($"{hostname}{RelativePath}"))
                .WithEnvironment("KC_HTTP_RELATIVE_PATH", RelativePath)
                // The management interface inherits KC_HTTP_RELATIVE_PATH when left unset,
                // which would move the readiness probe below to /auth/health/ready. Pinned so
                // the two can move independently.
                .WithEnvironment("KC_HTTP_MANAGEMENT_RELATIVE_PATH", "/")
                // Restates, for Compose, the readiness check AddKeycloak already declares in
                // the model: the publisher does not translate WithHttpHealthCheck, so without
                // this the consumers Aspire wires up only ever wait for the container to have
                // started. KC_HEALTH_ENABLED and the management port both come with AddKeycloak.
                //
                // Shape taken from Keycloak's own health documentation, which recommends this
                // because the image ships no CLI HTTP client: bash opens a socket on the
                // management port and speaks HTTP/1.0 across it, which needs no Host header
                // and is closed by the server. Invoked through bash explicitly rather than via
                // CMD-SHELL, since /dev/tcp is a bash feature and /bin/sh is not bash in every
                // image. The status line is matched loosely on version because a server may
                // answer 1.0 with 1.1.
                .WithComposeHealthcheck(new Healthcheck
                {
                    Test =
                    [
                        "CMD", "bash", "-c",
                        @"{ printf 'HEAD /health/ready HTTP/1.0\r\n\r\n' >&0; grep -q '^HTTP/1\.[01] 200'; } 0<>/dev/tcp/localhost/9000"
                    ],
                    Interval = "5s",
                    Timeout = "5s",
                    Retries = 12,
                    // A first start creates the Keycloak schema and imports the realm, and
                    // failures inside the start period are not counted against the retries.
                    StartPeriod = "90s"
                });
        }
    }
}

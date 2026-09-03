using Aspire.Hosting.Docker.Resources.ServiceNodes;
using GroupSplit.AppHost.Deployment;

namespace GroupSplit.AppHost.Keycloak;

public static class KeycloakDeploymentExtensions
{
    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Prepares Keycloak to run outside local development.
        /// <para>
        /// <c>WithRealmImport</c> relies on development-time container file
        /// injection that <c>aspire publish</c> cannot express: it degrades to a
        /// bind mount pointing at a host path that will not exist on the target
        /// machine. The realm and theme are shipped as Compose configs instead,
        /// which keeps the stock upstream image and avoids pushing a derived one.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> AsDeployedKeycloak(
            IResourceBuilder<ParameterResource> hostname)
        {
            return keycloak
                // Production mode serves HTTPS only unless plain HTTP is opted into. TLS is
                // terminated upstream, so Keycloak speaks plain HTTP and learns the original
                // scheme and host from the forwarded headers the proxy sets.
                .WithEnvironment("KC_HTTP_ENABLED", "true")
                .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
                .WithEnvironment("KC_HOSTNAME", hostname)
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

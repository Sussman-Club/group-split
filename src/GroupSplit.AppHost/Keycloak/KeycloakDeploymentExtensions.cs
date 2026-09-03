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
                .WithEnvironment("KC_HOSTNAME", hostname);
        }
    }
}

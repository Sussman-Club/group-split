namespace GroupSplit.AppHost.Keycloak;

#pragma warning disable ASPIREDOCKERFILEBUILDER001

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
        /// machine. Copying the realm and theme into the image is the documented
        /// alternative, so this builds a derived image from the same base tag the
        /// hosting integration already selected.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> AsDeployedKeycloak(
            IResourceBuilder<ParameterResource> hostname)
        {
            var image = keycloak.Resource.Annotations
                .OfType<ContainerImageAnnotation>()
                .Last();

            var baseImage = image.Registry is null
                ? $"{image.Image}:{image.Tag}"
                : $"{image.Registry}/{image.Image}:{image.Tag}";

            return keycloak
                .WithDockerfileBuilder(".", context =>
                {
                    context.Builder
                        .From(baseImage)
                        .Copy("realms.json", "/opt/keycloak/data/import/realms.json")
                        .Copy("keycloak-themes/group-split", "/opt/keycloak/themes/group-split");
                })
                // Production mode serves HTTPS only unless plain HTTP is opted into. TLS is
                // terminated upstream, so Keycloak speaks plain HTTP and learns the original
                // scheme and host from the forwarded headers the proxy sets.
                .WithEnvironment("KC_HTTP_ENABLED", "true")
                .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
                .WithEnvironment("KC_HOSTNAME_STRICT", "false")
                .WithEnvironment("KC_HOSTNAME", hostname);
        }
    }
}

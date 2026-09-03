using Microsoft.Extensions.Configuration;

namespace GroupSplit.AppHost.Keycloak;

#pragma warning disable ASPIREDOCKERFILEBUILDER001

public static class KeycloakDeploymentExtensions
{
    private const string HostnameConfigurationKey = "Keycloak:Hostname";

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
        public IResourceBuilder<KeycloakResource> AsDeployedKeycloak(IConfiguration configuration)
        {
            var image = keycloak.Resource.Annotations
                .OfType<ContainerImageAnnotation>()
                .Last();

            var baseImage = image.Registry is null
                ? $"{image.Image}:{image.Tag}"
                : $"{image.Registry}/{image.Image}:{image.Tag}";

            // The browser and the back channel reach Keycloak by different names, so the
            // issuer has to be pinned to the address users actually hit. Left empty,
            // Keycloak falls back to the request's Host header.
            var hostname = keycloak.ApplicationBuilder.AddParameter(
                "keycloak-hostname",
                () => configuration[HostnameConfigurationKey] ?? string.Empty);

            return keycloak
                .WithDockerfileBuilder(".", context =>
                {
                    context.Builder
                        .From(baseImage)
                        .Copy("realms.json", "/opt/keycloak/data/import/realms.json")
                        .Copy("keycloak-themes/group-split", "/opt/keycloak/themes/group-split");
                })
                // Production mode serves HTTPS only unless plain HTTP is opted into.
                .WithEnvironment("KC_HTTP_ENABLED", "true")
                .WithEnvironment("KC_HOSTNAME_STRICT", "false")
                .WithEnvironment("KC_HOSTNAME", hostname);
        }
    }
}

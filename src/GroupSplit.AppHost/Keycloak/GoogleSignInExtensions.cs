using Microsoft.Extensions.Configuration;

namespace GroupSplit.AppHost.Keycloak;

public static class GoogleSignInExtensions
{
    private const string ClientIdParameterName = "google-client-id";

    private const string ClientSecretParameterName = "google-client-secret";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Wires Google as an identity provider for the imported realm.
        /// <para>
        /// Keycloak substitutes these into the <c>${...}</c> placeholders in
        /// realms.json at import time, so no credential is committed. Without
        /// credentials the provider imports disabled and hidden, leaving the
        /// login page unchanged.
        /// </para>
        /// <para>
        /// Read under <c>Parameters:</c>, the one spelling both homes already reach:
        /// user secrets set it directly, and the deploy workflow exports every
        /// GitHub secret as <c>Parameters__&lt;name&gt;</c> as well as its own name.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithGoogleSignIn(IConfiguration configuration)
        {
            var clientIdValue = configuration[$"Parameters:{ClientIdParameterName}"] ?? string.Empty;
            var clientSecretValue = configuration[$"Parameters:{ClientSecretParameterName}"] ?? string.Empty;

            var configured = !string.IsNullOrWhiteSpace(clientIdValue)
                             && !string.IsNullOrWhiteSpace(clientSecretValue);

            // Parameters rather than raw strings: the dashboard masks the secret
            // and the values flow into the manifest when this model is published.
            var clientId = keycloak.ApplicationBuilder
                .AddParameter(ClientIdParameterName, () => clientIdValue);

            var clientSecret = keycloak.ApplicationBuilder
                .AddParameter(ClientSecretParameterName, () => clientSecretValue, secret: true);

            return keycloak
                .WithEnvironment("GS_GOOGLE_ENABLED", configured ? "true" : "false")
                .WithEnvironment("GS_GOOGLE_HIDDEN", configured ? "false" : "true")
                .WithEnvironment("GS_GOOGLE_CLIENT_ID", clientId)
                .WithEnvironment("GS_GOOGLE_CLIENT_SECRET", clientSecret);
        }
    }
}

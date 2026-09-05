#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

public static class GoogleSignInExtensions
{
    // The names double as the env var names realms.json interpolates, in screaming-snake
    // form. See WithSmtpEnvironment for why the two spellings have to agree.
    private const string EnabledParameterName = "google-sign-in-enabled";

    private const string ClientIdParameterName = "google-client-id";

    private const string ClientSecretParameterName = "google-client-secret";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Wires Google as an identity provider for the imported realm, behind a switch.
        /// <para>
        /// Three parameters: whether the provider is on, and the OAuth client ID and secret
        /// it needs when it is. All are optional, so a deployment that never mentions Google
        /// gets a disabled provider rather than a prompt. Keycloak substitutes them into the
        /// <c>${...}</c> placeholders in realms.json at import time, so no credential is
        /// committed.
        /// </para>
        /// <para>
        /// The switch is a parameter of its own rather than inferred from the credentials
        /// being present, so this method only declares parameters and passes them through.
        /// Whether the values agree -- a provider that is on has to have credentials -- is
        /// checked once they are resolved, in the <c>validate-google-sign-in</c> pipeline step.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithGoogleSignIn()
        {
            var builder = keycloak.ApplicationBuilder;

            var enabled = builder.AddOptionalParameter(EnabledParameterName, "false")
                .WithDescription(
                    "Whether Google is offered on the login page (true/false). "
                    + "Needs google-client-id and google-client-secret when true.");

            var clientId = builder.AddOptionalParameter(ClientIdParameterName, string.Empty)
                .WithDescription("OAuth client ID from the Google Cloud console.");

            var clientSecret = builder.AddOptionalParameter(ClientSecretParameterName, string.Empty, secret: true)
                .WithDescription("OAuth client secret from the Google Cloud console.");

            builder.Pipeline.AddStep(
                "validate-google-sign-in",
                context => enabled.RequireValuesWhenEnabledAsync(context, [clientId, clientSecret]),
                dependsOn: WellKnownPipelineSteps.ProcessParameters,
                requiredBy: WellKnownPipelineSteps.BuildPrereq);

            return keycloak
                .WithEnvironment("GOOGLE_SIGN_IN_ENABLED", enabled)
                .WithEnvironment("GOOGLE_CLIENT_ID", clientId)
                .WithEnvironment("GOOGLE_CLIENT_SECRET", clientSecret);
        }
    }
}

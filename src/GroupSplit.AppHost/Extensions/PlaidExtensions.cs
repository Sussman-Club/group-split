#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

public static class PlaidExtensions
{
    // The names match the screaming-snake deployment variables the workflow turns into
    // Parameters__* entries: plaid-client-id pairs with PLAID_CLIENT_ID.
    private const string EnabledParameterName = "plaid-enabled";

    private const string ClientIdParameterName = "plaid-client-id";

    private const string SecretParameterName = "plaid-secret";

    private const string EnvironmentParameterName = "plaid-env";

    private const string RedirectUriParameterName = "plaid-redirect-uri";

    private const string KeyRingCertificateParameterName = "bank-key-certificate";

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// Gives a service the credentials it needs to link banks through Plaid, behind a
        /// switch.
        /// <para>
        /// Whether bank sync is on, the client id and secret it needs when it is, which
        /// Plaid environment to talk to, and where an OAuth redirect returns. The redirect
        /// URI and bank key-ring certificate are optional; the enabled switch controls whether
        /// the Plaid credentials are required.
        /// </para>
        /// <para>
        /// The switch is a parameter rather than something inferred here, because this
        /// method only declares parameters and passes them through; whether the values agree
        /// is checked once they are resolved, in the <c>validate-plaid</c> pipeline step.
        /// Inside the API the rule is the other way round and needs no flag: a connector is
        /// registered when credentials are present, and bank sync is available exactly when
        /// a connector answers. Sending an empty client id is therefore the same thing as
        /// sending nothing, which is what makes the two ends agree.
        /// </para>
        /// <para>
        /// The names are Going.Plaid's own configuration section, so the API binds them with
        /// no mapping of ours in between.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> WithPlaid()
        {
            var builder = resource.ApplicationBuilder;

            var enabled = builder.AddParameter(EnabledParameterName)
                .WithDescription(
                    "Whether people can link a bank through Plaid (true/false). "
                    + "Needs plaid-client-id and plaid-secret when true.");

            var clientId = builder.AddParameter(ClientIdParameterName)
                .WithDescription("Plaid client_id, from the Plaid dashboard. The same across environments.");

            var secret = builder.AddParameter(SecretParameterName, secret: true)
                .WithDescription("Plaid secret for the environment named by plaid-env. One per environment.");

            var environment = builder.AddParameter(EnvironmentParameterName)
                .WithDescription("Which Plaid environment to talk to: Sandbox or Production.");

            // Plaid Link normally runs OAuth in a popup and never leaves the page. Set this
            // to the registered full-page redirect URI for institutions that require it;
            // the API refuses a value that points anywhere else.
            var redirectUri = builder.AddOptionalParameter(RedirectUriParameterName)
                // The path is BankLinkAddresses.OAuthReturnPath, written out because the
                // AppHost references none of the application's projects and a description
                // is documentation rather than behaviour -- the API's own validator is what
                // refuses a value that disagrees with it.
                .WithDescription(
                    "Where a bank's own sign-in page returns to, for OAuth institutions. The public "
                    + "origin followed by /bank/oauth, e.g. https://groupsplit.example.com/bank/oauth "
                    + "-- and the same address registered in the Plaid dashboard. Leave empty to keep "
                    + "the popup flow.");

            // What the Data Protection key ring is encrypted with. It is optional: without
            // it the API stores the ring unwrapped, which is acceptable for local development
            // and logged as a deployment warning.
            var keyCertificate = builder.AddOptionalParameter(
                KeyRingCertificateParameterName, secret: true)
                .WithDescription(
                    "PKCS#12 certificate, base64 encoded, that the bank access-token key ring is "
                    + "encrypted with. Generate one with the command in docs/development-and-deployment.md.");

            builder.Pipeline.AddStep(
                "validate-plaid",
                context => enabled.RequireValuesWhenEnabledAsync(context, [clientId, secret]),
                dependsOn: WellKnownPipelineSteps.ProcessParameters,
                requiredBy: WellKnownPipelineSteps.BuildPrereq);

            return resource
                .WithEnvironment("Plaid__ClientId", clientId)
                .WithEnvironment("Plaid__Secret", secret)
                .WithEnvironment("Plaid__Environment", environment)
                .WithEnvironment("Plaid__RedirectUri", redirectUri)
                .WithEnvironment("Banking__KeyRingCertificate", keyCertificate);
        }
    }
}

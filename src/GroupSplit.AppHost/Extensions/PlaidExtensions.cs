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
        /// Plaid environment to talk to, and where an OAuth redirect returns. All optional,
        /// so a deployment that never mentions Plaid gets an API that starts and reports
        /// bank sync as off rather than a prompt for a credential nobody has.
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

            var enabled = builder.AddOptionalParameter(EnabledParameterName, "false")
                .WithDescription(
                    "Whether people can link a bank through Plaid (true/false). "
                    + "Needs plaid-client-id and plaid-secret when true.");

            var clientId = builder.AddOptionalParameter(ClientIdParameterName, string.Empty)
                .WithDescription("Plaid client_id, from the Plaid dashboard. The same across environments.");

            var secret = builder.AddOptionalParameter(SecretParameterName, string.Empty, secret: true)
                .WithDescription("Plaid secret for the environment named by plaid-env. One per environment.");

            var environment = builder.AddOptionalParameter(EnvironmentParameterName, "Sandbox")
                .WithDescription("Which Plaid environment to talk to: Sandbox or Production.");

            // Empty by default, and that is the working configuration: Plaid Link runs OAuth
            // in a popup and never leaves the page. Setting it switches those institutions
            // to a full-page redirect, which only works because the web app serves the
            // return page -- so the value is not free text, and the API refuses one that
            // points anywhere else.
            var redirectUri = builder.AddOptionalParameter(RedirectUriParameterName, string.Empty)
                // The path is BankLinkAddresses.OAuthReturnPath, written out because the
                // AppHost references none of the application's projects and a description
                // is documentation rather than behaviour -- the API's own validator is what
                // refuses a value that disagrees with it.
                .WithDescription(
                    "Where a bank's own sign-in page returns to, for OAuth institutions. The public "
                    + "origin followed by /bank/oauth, e.g. https://groupsplit.example.com/bank/oauth "
                    + "-- and the same address registered in the Plaid dashboard. Leave empty to keep "
                    + "the popup flow.");

            // What the Data Protection key ring is encrypted with. Optional, because
            // locally there is usually none and an unwrapped ring is the ordinary
            // development posture; required once bank sync is on, because a deployment
            // without it stores tokens whose keys sit in the same database.
            //
            // Losing it loses the stored tokens and nothing else: everybody links again.
            var keyCertificate = builder.AddOptionalParameter(
                    KeyRingCertificateParameterName, string.Empty, secret: true)
                .WithDescription(
                    "PKCS#12 certificate, base64 encoded, that the bank access-token key ring is "
                    + "encrypted with. Generate one with the command in docs/development-and-deployment.md.");

            builder.Pipeline.AddStep(
                "validate-plaid",
                context => enabled.RequireValuesWhenEnabledAsync(context, [clientId, secret, keyCertificate]),
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

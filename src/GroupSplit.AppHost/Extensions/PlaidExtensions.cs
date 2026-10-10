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
        /// Plaid environment to talk to, where an OAuth redirect returns, and the certificate
        /// the key ring protecting the access tokens is wrapped with. All optional, so a
        /// deployment that never mentions Plaid gets an API that starts and reports bank sync
        /// as off rather than a prompt for a credential nobody has -- but a publish with bank
        /// sync on needs the credentials and the certificate.
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
                    + "Needs plaid-client-id, plaid-secret and bank-key-certificate when true.");

            var clientId = builder.AddOptionalParameter(ClientIdParameterName, string.Empty)
                .WithDescription("Plaid client_id, from the Plaid dashboard. The same across environments.");

            var secret = builder.AddOptionalParameter(SecretParameterName, string.Empty, secret: true)
                .WithDescription("Plaid secret for the environment named by plaid-env. One per environment.");

            var environment = builder.AddOptionalParameter(EnvironmentParameterName, "Sandbox")
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

            // What the Data Protection key ring is encrypted with. Optional, because
            // locally there is usually none and an unwrapped ring is the ordinary
            // development posture; required once bank sync is on, because a deployment
            // without it stores tokens whose keys sit in the same database, and one dump
            // carries both. The API refuses to start outside Development without it, so
            // the publish refuses first rather than shipping a release that cannot start.
            //
            // Losing it loses the stored tokens and nothing else: everybody links again.
            var keyCertificate = builder.AddOptionalParameter(
                KeyRingCertificateParameterName, secret: true)
                .WithDescription(
                    "PKCS#12 certificate, base64 encoded, that the bank access-token key ring is "
                    + "encrypted with. Required whenever bank sync is on outside development. Generate one "
                    + "with the command in docs/development-and-deployment.md.");

            builder.Pipeline.AddStep(
                "validate-plaid",
                async context =>
                {
                    await enabled.RequireValuesWhenEnabledAsync(context, [clientId, secret, keyCertificate]);
                    await RequireCertificateWithCredentialsAsync(context, clientId, secret, keyCertificate);
                },
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

    /// <summary>
    /// Fails the pipeline when both Plaid credentials have a value and the key-ring
    /// certificate does not, whatever <c>plaid-enabled</c> says.
    /// </summary>
    /// <remarks>
    /// The credentials are passed through unconditionally and the API registers Plaid
    /// whenever it has both, so credentials left in place behind a switch that is off still
    /// link banks -- and still store tokens. That API refuses to start without the
    /// certificate, so this is the same rule, met before any image is built rather than
    /// after the deploy.
    /// </remarks>
    private static async Task RequireCertificateWithCredentialsAsync(
        PipelineStepContext context,
        IResourceBuilder<ParameterResource> clientId,
        IResourceBuilder<ParameterResource> secret,
        IResourceBuilder<ParameterResource> keyCertificate)
    {
        var ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(await clientId.Resource.GetValueAsync(ct))
            || string.IsNullOrWhiteSpace(await secret.Resource.GetValueAsync(ct))
            || !string.IsNullOrWhiteSpace(await keyCertificate.Resource.GetValueAsync(ct)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{clientId.Resource.Name} and {secret.Resource.Name} are set, so the API links banks whatever "
            + $"{EnabledParameterName} says, but {keyCertificate.Resource.Name} has no value. Set it, or clear "
            + "the Plaid credentials.");
    }
}

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

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// Gives a service the credentials it needs to link banks through Plaid, behind a
        /// switch.
        /// <para>
        /// Four parameters: whether bank sync is on, the client id and secret it needs when
        /// it is, and which Plaid environment to talk to. All optional, so a deployment that
        /// never mentions Plaid gets an API that starts and reports bank sync as off rather
        /// than a prompt for a credential nobody has.
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

            builder.Pipeline.AddStep(
                "validate-plaid",
                context => enabled.RequireValuesWhenEnabledAsync(context, [clientId, secret]),
                dependsOn: WellKnownPipelineSteps.ProcessParameters,
                requiredBy: WellKnownPipelineSteps.BuildPrereq);

            return resource
                .WithEnvironment("Plaid__ClientId", clientId)
                .WithEnvironment("Plaid__Secret", secret)
                .WithEnvironment("Plaid__Environment", environment);
        }
    }
}

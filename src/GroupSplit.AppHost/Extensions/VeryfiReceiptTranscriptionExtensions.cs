#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

public static class VeryfiReceiptTranscriptionExtensions
{
    private const string EnabledParameterName = "veryfi-enabled";
    private const string ClientIdParameterName = "veryfi-client-id";
    private const string UsernameParameterName = "veryfi-username";
    private const string ApiKeyParameterName = "veryfi-api-key";
    private const string LogRawResponsesParameterName = "veryfi-log-raw-responses";

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>Passes Veryfi credentials to the API that reads receipt attachments.</summary>
        public IResourceBuilder<T> WithVeryfiReceiptTranscription()
        {
            var builder = resource.ApplicationBuilder;
            var enabled = builder.AddOptionalParameter(EnabledParameterName, "false")
                .WithDescription("Whether receipt transcription through Veryfi is enabled (true/false).");
            var clientId = builder.AddOptionalParameter(ClientIdParameterName, string.Empty, secret: true)
                .WithDescription("Veryfi client ID.");
            var username = builder.AddOptionalParameter(UsernameParameterName, string.Empty, secret: true)
                .WithDescription("Veryfi API username.");
            var apiKey = builder.AddOptionalParameter(ApiKeyParameterName, string.Empty, secret: true)
                .WithDescription("Veryfi API key.");
            var logRawResponses = builder.AddOptionalParameter(LogRawResponsesParameterName, "false")
                .WithDescription("Whether to log Veryfi's whole response, for diagnosing a misread receipt (true/false).");

            builder.Pipeline.AddStep(
                "validate-veryfi",
                context => enabled.RequireValuesWhenEnabledAsync(context, [clientId, username, apiKey]),
                dependsOn: WellKnownPipelineSteps.ProcessParameters,
                requiredBy: WellKnownPipelineSteps.BuildPrereq);

            return resource
                .WithEnvironment("Veryfi__Enabled", enabled)
                .WithEnvironment("Veryfi__ClientId", clientId)
                .WithEnvironment("Veryfi__Username", username)
                .WithEnvironment("Veryfi__ApiKey", apiKey)
                .WithEnvironment("Veryfi__LogRawResponses", logRawResponses);
        }
    }
}

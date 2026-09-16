#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.ApplicationModel;

namespace GroupSplit.AppHost.Extensions;

public static class AzureOpenAIReceiptTranscriptionExtensions
{
    private const string ProviderParameterName = "receipt-transcription-provider";
    private const string EnabledParameterName = "azure-openai-enabled";
    private const string EndpointParameterName = "azure-openai-endpoint";
    private const string ModelParameterName = "azure-openai-model";
    private const string ApiKeyParameterName = "azure-openai-api-key";
    private const string TimeoutSecondsParameterName = "azure-openai-timeout-seconds";
    private const string LogRawResponsesParameterName = "azure-openai-log-raw-responses";

    public sealed record Parameters(
        IResourceBuilder<ParameterResource> Provider,
        IResourceBuilder<ParameterResource> Enabled,
        IResourceBuilder<ParameterResource> Endpoint,
        IResourceBuilder<ParameterResource> Model,
        IResourceBuilder<ParameterResource> ApiKey,
        IResourceBuilder<ParameterResource> TimeoutSeconds,
        IResourceBuilder<ParameterResource> LogRawResponses);

    public static Parameters AddAzureOpenAIReceiptTranscriptionParameters(
        this IDistributedApplicationBuilder builder)
    {
        var provider = builder.AddOptionalParameter(ProviderParameterName, string.Empty)
            .WithDescription("Receipt provider: Veryfi or AzureOpenAI; empty keeps legacy provider fallback.");
        var enabled = builder.AddOptionalParameter(EnabledParameterName, "false")
            .WithDescription("Whether receipt transcription through Azure OpenAI is enabled (true/false).");
        var endpoint = builder.AddOptionalParameter(EndpointParameterName, string.Empty)
            .WithDescription("Azure OpenAI v1 base URL, for example https://resource.openai.azure.com/openai/v1/.");
        var model = builder.AddOptionalParameter(ModelParameterName, string.Empty)
            .WithDescription("Azure OpenAI deployment/model name.");
        var apiKey = builder.AddOptionalParameter(ApiKeyParameterName, string.Empty, secret: true)
            .WithDescription("Azure OpenAI API key.");
        var timeoutSeconds = builder.AddOptionalParameter(TimeoutSecondsParameterName, "120")
            .WithDescription("Azure OpenAI request timeout in seconds.");
        var logRawResponses = builder.AddOptionalParameter(LogRawResponsesParameterName, "false")
            .WithDescription("Enables safe response diagnostics without logging receipt content (true/false).");

        builder.Pipeline.AddStep(
            "validate-azure-openai",
            context => enabled.RequireValuesWhenEnabledAsync(context, [endpoint, model, apiKey]),
            dependsOn: WellKnownPipelineSteps.ProcessParameters,
            requiredBy: WellKnownPipelineSteps.BuildPrereq);

        return new Parameters(provider, enabled, endpoint, model, apiKey, timeoutSeconds, logRawResponses);
    }

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>Passes Azure OpenAI receipt transcription settings to the API.</summary>
        public IResourceBuilder<T> WithAzureOpenAIReceiptTranscription(Parameters parameters) => resource
            .WithEnvironment("ReceiptTranscription__Provider", parameters.Provider)
            .WithEnvironment("AzureOpenAI__Enabled", parameters.Enabled)
            .WithEnvironment("AzureOpenAI__TimeoutSeconds", parameters.TimeoutSeconds)
            .WithEnvironment("AzureOpenAI__LogRawResponses", parameters.LogRawResponses);
    }
}

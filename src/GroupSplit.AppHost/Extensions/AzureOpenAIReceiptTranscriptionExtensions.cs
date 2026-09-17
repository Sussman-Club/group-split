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

    public sealed record Parameters(
        IResourceBuilder<ParameterResource> Provider,
        IResourceBuilder<ParameterResource> Enabled,
        IResourceBuilder<ParameterResource> Endpoint,
        IResourceBuilder<ParameterResource> Model,
        IResourceBuilder<ParameterResource> ApiKey);

    public static Parameters AddAzureOpenAIReceiptTranscriptionParameters(
        this IDistributedApplicationBuilder builder)
    {
        var provider = builder.AddParameter(ProviderParameterName)
            .WithDescription("Receipt provider: Veryfi or AzureOpenAI.");
        var enabled = builder.AddParameter(EnabledParameterName)
            .WithDescription("Whether receipt transcription through Azure OpenAI is enabled (true/false).");
        var endpoint = builder.AddParameter(EndpointParameterName)
            .WithDescription("Azure OpenAI v1 base URL, for example https://resource.openai.azure.com/openai/v1/.");
        var model = builder.AddParameter(ModelParameterName)
            .WithDescription("Azure OpenAI deployment/model name.");
        var apiKey = builder.AddParameter(ApiKeyParameterName, secret: true)
            .WithDescription("Azure OpenAI API key.");

        builder.Pipeline.AddStep(
            "validate-azure-openai",
            context => enabled.RequireValuesWhenEnabledAsync(context, [endpoint, model, apiKey]),
            dependsOn: WellKnownPipelineSteps.ProcessParameters,
            requiredBy: WellKnownPipelineSteps.BuildPrereq);

        return new Parameters(provider, enabled, endpoint, model, apiKey);
    }

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// Passes the provider selector and Azure OpenAI feature switch to the API.
        /// The endpoint, key, and model continue to flow through the model reference.
        /// </summary>
        public IResourceBuilder<T> WithAzureOpenAIReceiptTranscription(Parameters parameters) => resource
            .WithEnvironment("ReceiptTranscription__Provider", parameters.Provider)
            .WithEnvironment("AzureOpenAI__Enabled", parameters.Enabled);
    }
}

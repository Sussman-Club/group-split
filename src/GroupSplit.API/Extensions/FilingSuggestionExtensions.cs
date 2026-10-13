using System.Data.Common;
using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Points the filing suggester at its own model reference.
/// </summary>
public static class FilingSuggestionExtensions
{
    /// <summary>The connection the AppHost gives the Decision-1 deployment.</summary>
    public const string ConnectionName = "filing-suggestions";

    /// <summary>Records GenAI message content on spans when <c>true</c>.</summary>
    public const string CaptureMessageContentVariable = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    /// <summary>
    /// The endpoint, key and deployment come from the <c>filing-suggestions</c> connection,
    /// and only while Azure OpenAI is switched on, so turning it off stops every call to
    /// Azure and not just the receipt ones.
    /// </summary>
    public static IServiceCollection AddFilingSuggestions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FilingSuggestionOptions>()
            .Bind(configuration.GetSection(FilingSuggestionOptions.SectionName))
            .PostConfigure(options =>
            {
                // The OpenTelemetry GenAI switch, the one the receipt reader's instrumentation
                // and every other GenAI library reads, rather than a setting of our own.
                options.CaptureMessageContent |= configuration.GetValue<bool>(CaptureMessageContentVariable);

                var azure = configuration.GetSection(AzureOpenAIReceiptTranscriptionOptions.SectionName)
                    .Get<AzureOpenAIReceiptTranscriptionOptions>();

                if (azure?.Enabled != true)
                {
                    options.Deployment = null;
                    return;
                }

                var connection = new DbConnectionStringBuilder
                {
                    ConnectionString = configuration.GetConnectionString(ConnectionName) ?? string.Empty
                };

                if (string.IsNullOrWhiteSpace(options.Deployment) && connection.TryGetValue("Model", out var model))
                    options.Deployment = model?.ToString();

                // The endpoint is the OpenAI v1 base, ending in /openai/v1/; the decision
                // route hangs off the resource itself.
                if (options.Endpoint is null
                    && connection.TryGetValue("Endpoint", out var endpoint)
                    && Uri.TryCreate(endpoint?.ToString(), UriKind.Absolute, out var uri))
                {
                    options.Endpoint = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
                }

                if (string.IsNullOrWhiteSpace(options.Key) && connection.TryGetValue("Key", out var key))
                    options.Key = key?.ToString();
            });

        return services;
    }
}

using System.ClientModel.Primitives;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;
using GroupSplit.API.Services.ReceiptTranscription.Veryfi;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using Polly;
using VeryfiReceiptTranscriptionProvider = GroupSplit.API.Services.ReceiptTranscription.Veryfi.VeryfiReceiptTranscriptionProvider;

namespace GroupSplit.API.Extensions;

/// <summary>Registers a document-understanding provider without coupling the domain service to it.</summary>
public static class ReceiptTranscriptionExtensions
{
    /// <summary>The resilience pipeline a provider's HTTP client runs in place of the standard one.</summary>
    public const string ResiliencePipelineName = "receipt-transcription";

    /// <summary>The keyed chat client and OpenAI client the Azure OpenAI provider reads through.</summary>
    public const string ChatClientName = "receipt-transcription";

    public static IServiceCollection AddReceiptTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(ReceiptTranscriptionOptions.SectionName));
        services.AddOptions<VeryfiReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(VeryfiReceiptTranscriptionOptions.SectionName));
        services.AddOptions<AzureOpenAIReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(AzureOpenAIReceiptTranscriptionOptions.SectionName));

        services.AddScoped<IReceiptTranscriptionService, ReceiptTranscriptionService>();

        // Not the standard handler the service defaults give every other client. That one
        // allows 10 s an attempt and 30 s in all, which a long receipt overruns, and then sends
        // the POST again while the person who asked is still waiting on the first. A reading
        // gets one attempt and the whole budget.
        // The client's own timeout is off so that the pipeline's is the only one, and running
        // out of it is always the same exception.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is marked experimental.
        services.AddHttpClient<VeryfiReceiptTranscriptionProvider>((serviceProvider, client) =>
            {
                var options = serviceProvider
                    .GetRequiredService<IOptions<VeryfiReceiptTranscriptionOptions>>()
                    .Value;
                client.BaseAddress = options.Endpoint;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .RemoveAllResilienceHandlers()
            .AddResilienceHandler(ResiliencePipelineName, (pipeline, context) =>
                pipeline.AddTimeout(context.ServiceProvider
                    .GetRequiredService<IOptions<ReceiptTranscriptionOptions>>()
                    .Value.Timeout));
#pragma warning restore EXTEXP0001
        services.AddKeyedScoped<IReceiptTranscriptionProvider>("Veryfi", (serviceProvider, _) =>
            serviceProvider.GetRequiredService<VeryfiReceiptTranscriptionProvider>());

        services.AddScoped<AzureOpenAIReceiptTranscriptionProvider>();
        services.AddKeyedScoped<IReceiptTranscriptionProvider>("AzureOpenAI", (serviceProvider, _) =>
            serviceProvider.GetRequiredService<AzureOpenAIReceiptTranscriptionProvider>());

        services.AddScoped<IReceiptTranscriptionProvider>(serviceProvider =>
        {
            var selection = serviceProvider
                .GetRequiredService<IOptions<ReceiptTranscriptionOptions>>()
                .Value;
            var veryfi = serviceProvider
                .GetRequiredService<IOptions<VeryfiReceiptTranscriptionOptions>>()
                .Value;
            var azure = serviceProvider
                .GetRequiredService<IOptions<AzureOpenAIReceiptTranscriptionOptions>>()
                .Value;

            var provider = selection.Provider.Trim();
            if (provider.Length == 0)
            {
                // Veryfi was the only provider before the selector existed. Keep it first when both
                // providers are configured so existing deployments do not change behavior silently.
                provider = IsVeryfiConfigured(veryfi)
                    ? "Veryfi"
                    : IsAzureOpenAiConfigured(azure) ? "AzureOpenAI" : string.Empty;
            }

            if (string.Equals(provider, "Veryfi", StringComparison.OrdinalIgnoreCase)
                && IsVeryfiConfigured(veryfi))
            {
                return serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("Veryfi");
            }

            if (string.Equals(provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase)
                && IsAzureOpenAiConfigured(azure))
            {
                return serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("AzureOpenAI");
            }

            return new UnavailableReceiptTranscriptionProvider();
        });

        return services;
    }

    /// <summary>
    /// The Azure OpenAI client the provider reads through, held to the same rule as Veryfi:
    /// one attempt, and the whole budget for it.
    /// </summary>
    /// <remarks>
    /// The OpenAI SDK does not go through <c>IHttpClientFactory</c>, so the service defaults'
    /// resilience handler never reached it; what it has instead is its own pipeline, which
    /// gives up on an attempt after 100 s and then retries it up to three times.
    /// </remarks>
    public static IHostApplicationBuilder AddReceiptTranscriptionChatClient(this IHostApplicationBuilder builder)
    {
        var budget = builder.Configuration
            .GetSection(ReceiptTranscriptionOptions.SectionName)
            .Get<ReceiptTranscriptionOptions>()?.Timeout ?? new ReceiptTranscriptionOptions().Timeout;

        builder.AddKeyedOpenAIClient(name: ChatClientName, configureOptions: options => UseOneAttempt(options, budget))
            .AddKeyedChatClient(ChatClientName)
            .UseOpenTelemetry()
            .UseLogging();

        return builder;
    }

    /// <summary>One attempt, which may take the whole budget.</summary>
    internal static void UseOneAttempt(OpenAIClientOptions options, TimeSpan budget)
    {
        options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        options.NetworkTimeout = budget;
    }

    private static bool IsVeryfiConfigured(VeryfiReceiptTranscriptionOptions options) =>
        options.Enabled
        && !string.IsNullOrWhiteSpace(options.ClientId)
        && !string.IsNullOrWhiteSpace(options.Username)
        && !string.IsNullOrWhiteSpace(options.ApiKey);

    private static bool IsAzureOpenAiConfigured(AzureOpenAIReceiptTranscriptionOptions options) => options.Enabled;
}

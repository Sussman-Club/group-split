using System.ClientModel.Primitives;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace GroupSplit.API.Extensions;

/// <summary>Registers a document-understanding provider without coupling the domain service to it.</summary>
public static class ReceiptTranscriptionExtensions
{
    /// <summary>The keyed chat client and OpenAI client the Azure OpenAI provider reads through.</summary>
    public const string ChatClientName = "receipt-transcription";

    public static IServiceCollection AddReceiptTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(ReceiptTranscriptionOptions.SectionName));
        services.AddOptions<AzureOpenAIReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(AzureOpenAIReceiptTranscriptionOptions.SectionName));

        services.AddScoped<IReceiptTranscriptionService, ReceiptTranscriptionService>();

        services.AddScoped<AzureOpenAIReceiptTranscriptionProvider>();
        services.AddKeyedScoped<IReceiptTranscriptionProvider>("AzureOpenAI", (serviceProvider, _) =>
            serviceProvider.GetRequiredService<AzureOpenAIReceiptTranscriptionProvider>());

        services.AddScoped<IReceiptTranscriptionProvider>(serviceProvider =>
        {
            var azure = serviceProvider.GetRequiredService<IOptions<AzureOpenAIReceiptTranscriptionOptions>>().Value;
            if (azure.Enabled)
            {
                return serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("AzureOpenAI");
            }

            return new UnavailableReceiptTranscriptionProvider();
        });

        return services;
    }

    /// <summary>
    /// The Azure OpenAI client gets one attempt and the whole budget for it.
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

}

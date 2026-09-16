using GroupSplit.API.Services;
using GroupSplit.API.Services.AzureOpenAI;
using GroupSplit.API.Services.Veryfi;
using Microsoft.Extensions.Http.Resilience;

namespace GroupSplit.API.Extensions;

/// <summary>Registers a document-understanding provider without coupling the domain service to it.</summary>
public static class ReceiptTranscriptionExtensions
{
    public static IServiceCollection AddReceiptTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        var selection = configuration.GetSection(ReceiptTranscriptionOptions.SectionName)
            .Get<ReceiptTranscriptionOptions>() ?? new ReceiptTranscriptionOptions();
        var veryfiSection = configuration.GetSection(VeryfiReceiptTranscriptionOptions.SectionName);
        var veryfi = veryfiSection.Get<VeryfiReceiptTranscriptionOptions>()
            ?? new VeryfiReceiptTranscriptionOptions();
        var azureSection = configuration.GetSection(AzureOpenAIReceiptTranscriptionOptions.SectionName);
        var azure = azureSection.Get<AzureOpenAIReceiptTranscriptionOptions>()
            ?? new AzureOpenAIReceiptTranscriptionOptions();
        azure.ConnectionString = configuration.GetConnectionString(
            AzureOpenAIReceiptTranscriptionOptions.ConnectionStringName) ?? string.Empty;

        services.AddScoped<IReceiptTranscriptionService, ReceiptTranscriptionService>();

        var provider = selection.Provider.Trim();
        if (provider.Length == 0)
        {
            // Veryfi was the only provider before the selector existed. Keep it first when both
            // providers are configured so existing deployments do not change behavior silently.
            provider = IsVeryfiConfigured(veryfi)
                ? "Veryfi"
                : IsAzureOpenAiConfigured(azure) ? "AzureOpenAI" : string.Empty;
        }

        if (IsVeryfiConfigured(veryfi))
        {
            services.Configure<VeryfiReceiptTranscriptionOptions>(veryfiSection);
            services.AddHttpClient<VeryfiReceiptTranscriptionProvider>(client =>
            {
                client.BaseAddress = veryfi.Endpoint;
                client.Timeout = TimeSpan.FromMinutes(2);
            });
            services.AddKeyedScoped<IReceiptTranscriptionProvider>("Veryfi", (serviceProvider, _) =>
                serviceProvider.GetRequiredService<VeryfiReceiptTranscriptionProvider>());
        }

        if (IsAzureOpenAiConfigured(azure))
        {
            services.Configure<AzureOpenAIReceiptTranscriptionOptions>(azureSection);
            services.PostConfigure<AzureOpenAIReceiptTranscriptionOptions>(options =>
                options.ConnectionString = azure.ConnectionString);
#pragma warning disable EXTEXP0001 // Required to replace ServiceDefaults' generic resilience policy for this long-running POST.
            services.AddHttpClient(AzureOpenAIReceiptTranscriptionProvider.HttpClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(azure.TimeoutSeconds);
                })
                // ServiceDefaults adds the standard resilience handler to every HttpClient.
                // Receipt transcription is a long-running POST, so it needs its own timeout
                // and must not be retried automatically after a partial model request.
                .RemoveAllResilienceHandlers()
                .AddStandardResilienceHandler(options =>
                {
                    var timeout = TimeSpan.FromSeconds(azure.TimeoutSeconds);
                    options.Retry.DisableForUnsafeHttpMethods();
                    options.AttemptTimeout.Timeout = timeout;
                    options.TotalRequestTimeout.Timeout = timeout;
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromTicks(timeout.Ticks * 2);
                });
#pragma warning restore EXTEXP0001
            services.AddSingleton<IAzureOpenAIReceiptAgentFactory, AzureOpenAIReceiptAgentFactory>();
            services.AddScoped<AzureOpenAIReceiptTranscriptionProvider>();
            services.AddKeyedScoped<IReceiptTranscriptionProvider>("AzureOpenAI", (serviceProvider, _) =>
                serviceProvider.GetRequiredService<AzureOpenAIReceiptTranscriptionProvider>());
        }

        services.AddScoped<IReceiptTranscriptionProvider>(serviceProvider => provider switch
        {
            var name when string.Equals(name, "Veryfi", StringComparison.OrdinalIgnoreCase)
                && IsVeryfiConfigured(veryfi) =>
                serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("Veryfi"),
            var name when string.Equals(name, "AzureOpenAI", StringComparison.OrdinalIgnoreCase)
                && IsAzureOpenAiConfigured(azure) =>
                serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("AzureOpenAI"),
            _ => new UnavailableReceiptTranscriptionProvider()
        });
        return services;
    }

    private static bool IsVeryfiConfigured(VeryfiReceiptTranscriptionOptions options) =>
        options.Enabled
        && !string.IsNullOrWhiteSpace(options.ClientId)
        && !string.IsNullOrWhiteSpace(options.Username)
        && !string.IsNullOrWhiteSpace(options.ApiKey);

    private static bool IsAzureOpenAiConfigured(AzureOpenAIReceiptTranscriptionOptions options) =>
        options.Enabled
        && AzureOpenAIReceiptTranscriptionProvider.TryGetConnection(options.ConnectionString, out _)
        && options.TimeoutSeconds > 0;
}

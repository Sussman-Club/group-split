using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;
using GroupSplit.API.Services.ReceiptTranscription.Veryfi;
using Microsoft.Extensions.Options;
using VeryfiReceiptTranscriptionProvider = GroupSplit.API.Services.ReceiptTranscription.Veryfi.VeryfiReceiptTranscriptionProvider;

namespace GroupSplit.API.Extensions;

/// <summary>Registers a document-understanding provider without coupling the domain service to it.</summary>
public static class ReceiptTranscriptionExtensions
{
    public static IServiceCollection AddReceiptTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(ReceiptTranscriptionOptions.SectionName));
        services.AddOptions<VeryfiReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(VeryfiReceiptTranscriptionOptions.SectionName));
        services.AddOptions<AzureOpenAIReceiptTranscriptionOptions>()
            .Bind(configuration.GetSection(AzureOpenAIReceiptTranscriptionOptions.SectionName));

        services.AddScoped<IReceiptTranscriptionService, ReceiptTranscriptionService>();

        services.AddHttpClient<VeryfiReceiptTranscriptionProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<VeryfiReceiptTranscriptionOptions>>()
                .Value;
            client.BaseAddress = options.Endpoint;
            client.Timeout = TimeSpan.FromMinutes(2);
        });
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

    private static bool IsVeryfiConfigured(VeryfiReceiptTranscriptionOptions options) =>
        options.Enabled
        && !string.IsNullOrWhiteSpace(options.ClientId)
        && !string.IsNullOrWhiteSpace(options.Username)
        && !string.IsNullOrWhiteSpace(options.ApiKey);

    private static bool IsAzureOpenAiConfigured(AzureOpenAIReceiptTranscriptionOptions options) => options.Enabled;
}

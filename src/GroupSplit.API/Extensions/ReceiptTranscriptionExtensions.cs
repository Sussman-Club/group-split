using GroupSplit.API.Services;
using GroupSplit.API.Services.Veryfi;

namespace GroupSplit.API.Extensions;

/// <summary>Registers a document-understanding provider without coupling the domain service to it.</summary>
public static class ReceiptTranscriptionExtensions
{
    public static IServiceCollection AddReceiptTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(VeryfiReceiptTranscriptionOptions.SectionName);
        var veryfi = section.Get<VeryfiReceiptTranscriptionOptions>() ?? new VeryfiReceiptTranscriptionOptions();

        services.AddScoped<IReceiptTranscriptionService, ReceiptTranscriptionService>();

        if (!veryfi.Enabled
            || string.IsNullOrWhiteSpace(veryfi.ClientId)
            || string.IsNullOrWhiteSpace(veryfi.Username)
            || string.IsNullOrWhiteSpace(veryfi.ApiKey))
        {
            services.AddScoped<IReceiptTranscriptionProvider, UnavailableReceiptTranscriptionProvider>();
            return services;
        }

        services.Configure<VeryfiReceiptTranscriptionOptions>(section);
        services.AddHttpClient<VeryfiReceiptTranscriptionProvider>(client =>
        {
            client.BaseAddress = veryfi.Endpoint;
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        services.AddScoped<IReceiptTranscriptionProvider>(provider =>
            provider.GetRequiredService<VeryfiReceiptTranscriptionProvider>());
        return services;
    }
}

using GroupSplit.Shared;

namespace GroupSplit.App.Web.Services;

public static class ReceiptClientPolicies
{
    public static IServiceCollection AddReceiptClientPolicies(this IServiceCollection services)
    {
        // The global Aspire policy allows ten seconds per attempt and retries failures.
        // A receipt read needs its whole OCR budget; replaying its multipart POST also
        // tries to read an already-consumed browser stream. HttpClient owns the timeout.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental.
        services.AddHttpClient(nameof(IReceiptsClient)).RemoveAllResilienceHandlers();
        services.AddHttpClient(nameof(IInboxClient)).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        return services;
    }
}

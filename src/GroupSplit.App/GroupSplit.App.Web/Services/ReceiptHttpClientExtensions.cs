using GroupSplit.Shared;

#pragma warning disable EXTEXP0001

namespace GroupSplit.App.Web.Services;

public static class ReceiptHttpClientExtensions
{
    public static IHttpClientBuilder ConfigureReceiptHttpClient(this IServiceCollection services) =>
        services.AddHttpClient<IReceiptsClient, ReceiptsClient>()
            // Receipt reading can outlast the default ten-second attempt timeout. Upload
            // streams cannot be replayed safely, so use one bounded request without retries.
            .RemoveAllResilienceHandlers()
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));
}

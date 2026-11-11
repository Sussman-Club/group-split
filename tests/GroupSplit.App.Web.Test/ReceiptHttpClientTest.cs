using GroupSplit.App.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using System.Net;

namespace GroupSplit.App.Web.Test;

public class ReceiptHttpClientTest
{
    [Fact]
    public async Task Receipt_client_has_thirty_seconds_and_does_not_retry_failed_stream_uploads()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
        var handler = new FailingUploadHandler();
        var registration = services.ConfigureReceiptHttpClient().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(registration.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        using var content = new StreamContent(new MemoryStream([1, 2, 3]));
        using var response = await client.PostAsync("https://api/receipts/transcribe", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
        using var ordinary = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ordinary");
        Assert.Equal(Timeout.InfiniteTimeSpan, ordinary.Timeout);
    }

    private sealed class FailingUploadHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}

using System.Net;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Web.Services;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace GroupSplit.App.Web.Test.State;

public sealed class ReceiptClientPoliciesTest
{
    [Theory]
    [InlineData(nameof(IReceiptsClient))]
    [InlineData(nameof(IInboxClient))]
    public async Task Receipt_clients_allow_the_OCR_budget_without_replaying_a_failed_upload(string name)
    {
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
        services.AddSingleton<IClientOptionsSetter, TestOptions>();
        services.AddSharedServices();
        services.AddReceiptClientPolicies();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(name);
        Assert.Equal(TimeSpan.FromMinutes(3), client.Timeout);
        using var content = new StreamContent(new MemoryStream([1, 2, 3]));
        using var response = await client.PostAsync("receipts/transcribe", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(nameof(IReceiptsClient))]
    [InlineData(nameof(IInboxClient))]
    public async Task A_caller_can_still_cancel_a_receipt_request(string name)
    {
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
        services.AddSingleton<IClientOptionsSetter, TestOptions>();
        services.AddSharedServices();
        services.AddReceiptClientPolicies();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new WaitingHandler());
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsync("receipts/transcribe",
            new StringContent("receipt"), cancellation.Token));
    }

    private sealed class TestOptions : IClientOptionsSetter
    {
        public void ConfigureClient(HttpClientFactoryOptions options) =>
            options.HttpClientActions.Add(client => client.BaseAddress = new Uri("https://receipt.test/"));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

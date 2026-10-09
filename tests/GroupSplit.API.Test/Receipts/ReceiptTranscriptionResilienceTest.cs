using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services.ReceiptTranscription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using VeryfiReceiptTranscriptionOptions = GroupSplit.API.Services.ReceiptTranscription.Veryfi.VeryfiReceiptTranscriptionOptions;
using VeryfiReceiptTranscriptionProvider = GroupSplit.API.Services.ReceiptTranscription.Veryfi.VeryfiReceiptTranscriptionProvider;

namespace GroupSplit.API.Test.Receipts;

/// <summary>
/// A reading is one attempt with one budget. The standard resilience handler every other
/// client gets would retry the POST -- a second paid reading of the same file -- and give up
/// after 30 s, long before a long receipt is read; and a timeout must reach the person as the
/// documented retryable 502, not as a 500.
/// </summary>
public sealed class ReceiptTranscriptionResilienceTest
{
    [Fact]
    public async Task A_veryfi_reading_that_runs_out_of_time_answers_a_retryable_bad_gateway_after_one_attempt()
    {
        var veryfi = new CountingHandler(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });
        await using var services = Services(veryfi, TimeSpan.FromMilliseconds(200));
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IReceiptTranscriptionProvider>();

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(Document(),
            TestContext.Current.CancellationToken));

        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);
        Assert.Equal(1, veryfi.Calls);
    }

    [Fact]
    public async Task A_veryfi_refusal_is_not_sent_again_however_transient_it_looks()
    {
        var veryfi = new CountingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        }));
        await using var services = Services(veryfi, TimeSpan.FromMinutes(2));
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IReceiptTranscriptionProvider>();

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(Document(),
            TestContext.Current.CancellationToken));

        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);
        Assert.Equal(1, veryfi.Calls);
    }

    [Fact]
    public async Task The_veryfi_budget_is_two_minutes_unless_configured()
    {
        var services = new ServiceCollection();
        services.AddReceiptTranscription(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(2),
            provider.GetRequiredService<IOptions<ReceiptTranscriptionOptions>>().Value.Timeout);
    }

    /// <summary>
    /// What the HTTP client itself throws on its own timeout: a cancellation nobody asked for.
    /// </summary>
    [Fact]
    public async Task A_client_timeout_is_a_retryable_bad_gateway_but_a_caller_cancellation_stays_one()
    {
        using var timingOut = new HttpClient(new CountingHandler(_ =>
            throw new TaskCanceledException("The request timed out.", new TimeoutException())))
        {
            BaseAddress = new Uri("https://veryfi.test/")
        };
        var provider = Veryfi(timingOut);

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(Document(),
            TestContext.Current.CancellationToken));
        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var waiting = new HttpClient(new CountingHandler(async ct =>
        {
            await cancelled.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }))
        {
            BaseAddress = new Uri("https://veryfi.test/")
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Veryfi(waiting).Transcribe(Document(),
            cancelled.Token));
    }

    /// <summary>
    /// The OpenAI SDK has its own pipeline, outside the HTTP client factory: 100 s an attempt
    /// and three retries, unless told otherwise.
    /// </summary>
    [Fact]
    public async Task The_azure_openai_client_makes_one_attempt_with_the_whole_budget()
    {
        var azure = new CountingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        }));
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://azure-openai.test/openai/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(azure))
        };
        ReceiptTranscriptionExtensions.UseOneAttempt(options, TimeSpan.FromMinutes(2));
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), options).GetChatClient("test-model");

        await Assert.ThrowsAsync<ClientResultException>(() => client.CompleteChatAsync(
            [OpenAI.Chat.ChatMessage.CreateUserMessage("read this")],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, azure.Calls);
        Assert.Equal(TimeSpan.FromMinutes(2), options.NetworkTimeout);
    }

    private static ServiceProvider Services(CountingHandler veryfi, TimeSpan budget)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReceiptTranscription:Provider"] = "Veryfi",
                ["ReceiptTranscription:Timeout"] = budget.ToString("c"),
                ["Veryfi:Enabled"] = "true",
                ["Veryfi:ClientId"] = "client-id",
                ["Veryfi:Username"] = "username",
                ["Veryfi:ApiKey"] = "api-key",
                ["Veryfi:Endpoint"] = "https://veryfi.test/"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        // What the service defaults give every client in the running API.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddReceiptTranscription(configuration);
        services.AddHttpClient<VeryfiReceiptTranscriptionProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => veryfi);

        return services.BuildServiceProvider();
    }

    private static VeryfiReceiptTranscriptionProvider Veryfi(HttpClient client) =>
        new(client, Options.Create(new VeryfiReceiptTranscriptionOptions
        {
            Enabled = true,
            ClientId = "client-id",
            Username = "username",
            ApiKey = "api-key"
        }), NullLogger<VeryfiReceiptTranscriptionProvider>.Instance);

    private static ReceiptSourceDocument Document() =>
        new(Guid.NewGuid(), "receipt.jpg", "image/jpeg", [1, 2, 3]);

    private sealed class CountingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return respond(cancellationToken);
        }
    }
}

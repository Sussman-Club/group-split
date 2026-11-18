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

namespace GroupSplit.API.Test.Receipts;

/// <summary>
/// A reading is one attempt with one budget. The standard resilience handler every other
/// client gets would retry the POST -- a second paid reading of the same file -- and give up
/// after 30 s, long before a long receipt is read; and a timeout must reach the person as the
/// documented retryable 502, not as a 500.
/// </summary>
public sealed class ReceiptTranscriptionResilienceTest
{
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

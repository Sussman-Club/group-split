using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.API.Test.Receipts;

public sealed class AzureOpenAIReceiptTranscriptionProviderTest
{
    [Fact]
    public async Task Sends_the_receipt_to_the_keyed_chat_client_and_maps_structured_output()
    {
        var client = new StubChatClient(
            "{\"subtotal\":16.00,\"tax\":1.60,\"tip\":0,\"total\":17.60,\"discount\":2.00," +
            "\"items\":[{\"name\":\"Pizza\",\"unitPrice\":18.00,\"quantity\":1," +
            "\"totalPrice\":16.00,\"discountAmount\":2.00,\"taxAmount\":1.60}]}");
        var provider = new AzureOpenAIReceiptTranscriptionProvider(
            client,
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1, 2, 3]),
            TestContext.Current.CancellationToken);

        Assert.Equal(17.60m, receipt.Total);
        var item = Assert.Single(receipt.Items);
        Assert.Equal("Pizza", item.Name);
        Assert.Equal(1.60m, item.TaxAmount);
        Assert.Contains("discount", client.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dinner.jpg", client.AttachmentDescription);
    }

    [Fact]
    public async Task Converts_chat_client_failures_to_a_retryable_bad_gateway()
    {
        var provider = new AzureOpenAIReceiptTranscriptionProvider(
            new StubChatClient(new HttpRequestException("Azure is unavailable")),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1]),
            TestContext.Current.CancellationToken));

        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);
    }

    [Fact]
    public void Leaves_transcription_unavailable_when_azure_openai_is_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReceiptTranscription:Provider"] = "AzureOpenAI",
                ["AzureOpenAI:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddReceiptTranscription(configuration);

        using var provider = services.BuildServiceProvider();
        var transcriptionProvider = provider.GetRequiredService<IReceiptTranscriptionProvider>();

        Assert.Equal("unconfigured", transcriptionProvider.Name);
    }

    [Fact]
    public void Resolves_the_selected_provider_from_the_same_keyed_chat_client_used_in_production()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReceiptTranscription:Provider"] = "AzureOpenAI",
                ["AzureOpenAI:Enabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddReceiptTranscription(configuration);
        services.AddKeyedSingleton<IChatClient>(
            "receipt-transcription",
            new StubChatClient("{\"items\":[]}"));

        using var serviceProvider = services.BuildServiceProvider();
        var selected = serviceProvider.GetRequiredService<IReceiptTranscriptionProvider>();
        var keyed = serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("AzureOpenAI");

        Assert.Equal("Azure OpenAI", selected.Name);
        Assert.Same(selected, keyed);
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly string? response;
        private readonly Exception? failure;

        public StubChatClient(string response) => this.response = response;

        public StubChatClient(Exception failure) => this.failure = failure;

        public string Prompt { get; private set; } = string.Empty;

        public string AttachmentDescription { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var message = messages.Single();
            Prompt = string.Join("\n", message.Contents.OfType<TextContent>().Select(content => content.Text));
            AttachmentDescription = string.Join(
                "\n",
                message.Contents.OfType<DataContent>().Select(content => content.Name ?? string.Empty));

            if (failure is not null)
                throw failure;

            var assistant = new ChatMessage(ChatRole.Assistant, response!);
            return Task.FromResult(new ChatResponse(assistant));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

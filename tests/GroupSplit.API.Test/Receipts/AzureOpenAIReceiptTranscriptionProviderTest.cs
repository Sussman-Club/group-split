using System.Net;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.AzureOpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Test.Receipts;

public sealed class AzureOpenAIReceiptTranscriptionProviderTest
{
    [Fact]
    public async Task Posts_an_image_with_the_api_key_and_maps_structured_output()
    {
        var server = new AzureOpenAIServer("""
            {
              "output": [
                {
                  "type": "message",
                  "role": "assistant",
                  "status": "completed",
                  "content": [
                    { "type": "output_text", "text": "{\"subtotal\":18.00,\"tax\":1.80,\"tip\":0,\"total\":19.80,\"items\":[{\"name\":\"Pizza\",\"unitPrice\":18.00,\"quantity\":1,\"totalPrice\":18.00,\"taxAmount\":1.80}]}" }
                  ]
                }
              ]
            }
            """);
        var provider = CreateProvider(server);

        var receipt = await provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1, 2, 3]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, server.Method);
        Assert.Equal("https://resource.openai.azure.com/openai/v1/responses", server.RequestUri!.ToString());
        Assert.Equal("test-api-key", server.ApiKey);
        Assert.Equal(19.80m, receipt.Total);
        var item = Assert.Single(receipt.Items);
        Assert.Equal("Pizza", item.Name);
        Assert.Equal(1.80m, item.TaxAmount);

        using var request = JsonDocument.Parse(server.Body);
        var root = request.RootElement;
        Assert.Equal("receipt-deployment", root.GetProperty("model").GetString());
        var image = root.GetProperty("input")[0].GetProperty("content")[1];
        Assert.Equal("input_image", image.GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,AQID", image.GetProperty("image_url").GetString());

        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.TryGetProperty("strict", out var strict), server.Body);
        Assert.True(strict.GetBoolean());
        Assert.Equal("AzureReceipt", format.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Sends_a_pdf_as_an_input_file_and_reads_nested_output_text()
    {
        var server = new AzureOpenAIServer("""
            {
              "output": [
                {
                  "type": "message",
                  "role": "assistant",
                  "status": "completed",
                  "content": [
                    { "type": "output_text", "text": "{\"subtotal\":100,\"tax\":20,\"tip\":0,\"total\":120,\"items\":[{\"name\":\"Shelf\",\"unitPrice\":100,\"quantity\":1,\"totalPrice\":100,\"taxAmount\":20}]}" }
                  ]
                }
              ]
            }
            """);
        var provider = CreateProvider(server);

        var receipt = await provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "ikea.pdf", "application/pdf", [4, 5]),
            TestContext.Current.CancellationToken);

        Assert.Equal(120m, receipt.Total);
        using var request = JsonDocument.Parse(server.Body);
        var file = request.RootElement.GetProperty("input")[0].GetProperty("content")[1];
        Assert.Equal("input_file", file.GetProperty("type").GetString());
        Assert.Equal("data:application/pdf;base64,BAU=", file.GetProperty("file_data").GetString());
        Assert.Equal("ikea.pdf", file.GetProperty("filename").GetString());
    }

    [Fact]
    public async Task Converts_provider_failures_to_a_retryable_bad_gateway()
    {
        var provider = CreateProvider(new AzureOpenAIServer("{}", HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1]),
            TestContext.Current.CancellationToken));

        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);
    }

    [Fact]
    public void Leaves_transcription_unavailable_when_azure_openai_is_disabled_or_unconfigured()
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
    public void Resolves_the_selected_provider_from_its_string_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReceiptTranscription:Provider"] = "AzureOpenAI",
                ["AzureOpenAI:Enabled"] = "true",
                ["ConnectionStrings:azure-openai-receipt-transcription"] =
                    "Endpoint=https://resource.openai.azure.com/openai/v1/;Key=test-api-key;Model=receipt-deployment"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddReceiptTranscription(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var selected = serviceProvider.GetRequiredService<IReceiptTranscriptionProvider>();
        var keyed = serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("AzureOpenAI");

        Assert.Equal("Azure OpenAI", selected.Name);
        Assert.Same(selected, keyed);
    }

    private static AzureOpenAIReceiptTranscriptionProvider CreateProvider(AzureOpenAIServer server)
    {
        var client = new HttpClient(server)
        {
            BaseAddress = new Uri("https://resource.openai.azure.com/openai/v1/")
        };
        return new AzureOpenAIReceiptTranscriptionProvider(
            new AzureOpenAIReceiptAgentFactory(new StubHttpClientFactory(client)),
            Options.Create(new AzureOpenAIReceiptTranscriptionOptions
            {
                Enabled = true,
                ConnectionString = "Endpoint=https://resource.openai.azure.com/openai/v1/;Key=test-api-key;Model=receipt-deployment"
            }),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class AzureOpenAIServer(string response, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string ApiKey { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.GetValues("api-key").Single();
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;
using Polly.Timeout;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace GroupSplit.API.Services.AzureOpenAI;

/// <summary>Azure OpenAI receipt transcription settings supplied by the AppHost.</summary>
public sealed class AzureOpenAIReceiptTranscriptionOptions
{
    public const string SectionName = "AzureOpenAI";
    public const string ConnectionStringName = "azure-openai-receipt-transcription";

    public bool Enabled { get; set; }
    /// <summary>
    /// The Aspire model-reference connection string. It contains Endpoint, Key, and ModelName.
    /// It is populated from ConnectionStrings__azure-openai-receipt-transcription.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Enables safe response diagnostics. The response body is never written to logs because it
    /// contains receipt content; only its size and a non-reversible hash are logged.
    /// </summary>
    public bool LogRawResponses { get; set; }
}

public sealed record AzureOpenAIReceiptConnection(string Endpoint, string ApiKey, string Model);

public interface IAzureOpenAIReceiptAgentFactory
{
    AIAgent Create(AzureOpenAIReceiptConnection connection, TimeSpan timeout);
}

/// <summary>Builds a Microsoft Agent Framework agent backed by Azure OpenAI Responses.</summary>
internal sealed class AzureOpenAIReceiptAgentFactory(IHttpClientFactory httpClients)
    : IAzureOpenAIReceiptAgentFactory
{
#pragma warning disable OPENAI001 // Azure OpenAI Responses support is currently marked experimental by the OpenAI SDK.
    public AIAgent Create(AzureOpenAIReceiptConnection connection, TimeSpan timeout)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(connection.Endpoint, UriKind.Absolute),
            NetworkTimeout = timeout,
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(httpClients.CreateClient(
                AzureOpenAIReceiptTranscriptionProvider.HttpClientName))
        };

        var auth = ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(
            new ApiKeyCredential(connection.ApiKey), "api-key");
        var client = new OpenAIClient(auth, clientOptions);

        return client.GetResponsesClient()
            .AsAIAgent(
                model: connection.Model,
                instructions: "Extract a normalized receipt from the supplied document.",
                name: "ReceiptTranscription")
            .AsBuilder()
            .UseOpenTelemetry("GroupSplit.ReceiptTranscription")
            .Build(null);
    }
#pragma warning restore OPENAI001
}

/// <summary>Reads receipt documents through Azure OpenAI using Microsoft Agent Framework.</summary>
public sealed class AzureOpenAIReceiptTranscriptionProvider(
    IAzureOpenAIReceiptAgentFactory agentFactory,
    IOptions<AzureOpenAIReceiptTranscriptionOptions> options,
    ILogger<AzureOpenAIReceiptTranscriptionProvider> logger) : IReceiptTranscriptionProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const string HttpClientName = "AzureOpenAIReceiptTranscription";

    private const string Prompt = """
        Read this receipt and return the normalized receipt JSON requested by the schema. Include
        every positive, purchasable product or merchandise line that the merchant printed. The
        Set each item's name to the human-readable merchant or product description printed on the
        receipt. Do not invent a product name that is not supported by the receipt.

        Capture discounts and coupons. Set discount to the total positive discount amount, and
        set discountAmount on each item when the receipt attributes a discount to that item.
        Do not return discount, coupon, refund, payment, or tender rows as purchasable items.
        Return each item's totalPrice after its applicable discount, and return subtotal as the
        sum of those net item totals after receipt-wide discounts have been allocated across the
        items. The item totals and subtotal must represent what was actually charged.

        Use the printed quantity when present; otherwise use 1. Include taxes attached to each
        line in taxAmount and receipt-level tax in tax. Use zero for an absent discount, tip, or
        tax. Return no prose outside the JSON object.
        """;

    public string Name => "Azure OpenAI";

    public async Task<TranscribedReceipt> Transcribe(
        ReceiptSourceDocument document, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!TryGetConnection(settings.ConnectionString, out var connection))
        {
            throw new BadGatewayException(
                ErrorCodes.ReceiptTranscriptionProviderUnavailable,
                "The receipt transcription service is not configured.");
        }

        var message = new ChatMessage(ChatRole.User, new List<AIContent>
        {
            new TextContent(Prompt),
            new DataContent(document.Content, document.ContentType) { Name = document.FileName }
        });

        try
        {
            var response = await agentFactory.Create(
                    connection, TimeSpan.FromSeconds(settings.TimeoutSeconds))
                .RunAsync<AzureReceipt>(
                message,
                serializerOptions: Json,
                options: new ChatClientAgentRunOptions(new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["strict"] = true }
                }),
                cancellationToken: ct);
            if (settings.LogRawResponses)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.Text)));
                logger.LogInformation(
                    "Azure OpenAI response diagnostics for receipt attachment {AttachmentId}: {PayloadLength} bytes, SHA-256 {PayloadHash}.",
                    document.AttachmentId, response.Text.Length, hash);
            }

            var receipt = response.Result;

            logger.LogInformation(
                "Azure OpenAI mapped {MappedItemCount} receipt line item(s) and {DiscountAmount} in discounts for attachment {AttachmentId}.",
                receipt.Items.Count, receipt.Discount, document.AttachmentId);

            return new TranscribedReceipt(
                receipt.Subtotal,
                receipt.Tax,
                receipt.Tip,
                receipt.Total,
                receipt.Items.Select(item => new TranscribedReceiptItem(
                    item.Name,
                    item.UnitPrice,
                    item.Quantity,
                    item.TotalPrice,
                    item.TaxAmount)).ToArray());
        }
        catch (BadGatewayException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException
            or JsonException
            or InvalidOperationException
            or ClientResultException
            or TimeoutRejectedException)
        {
            logger.LogWarning(
                error,
                "Azure OpenAI could not transcribe receipt attachment {AttachmentId}.",
                document.AttachmentId);
            throw new BadGatewayException(
                ErrorCodes.ReceiptTranscriptionProviderUnavailable,
                "The receipt transcription service could not be reached. Please try again shortly.",
                error);
        }
    }

    internal static bool TryGetConnection(string connectionString, out AzureOpenAIReceiptConnection connection)
    {
        connection = default!;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var endpoint = builder.TryGetValue("Endpoint", out var endpointValue) ? endpointValue?.ToString() : null;
            var apiKey = builder.TryGetValue("Key", out var keyValue) ? keyValue?.ToString() : null;
            // Aspire's OpenAI model reference uses Model in the injected connection string.
            // Accept ModelName as well for older/manual connection strings.
            var model = builder.TryGetValue("Model", out var modelValue)
                ? modelValue?.ToString()
                : builder.TryGetValue("ModelName", out var modelNameValue)
                    ? modelNameValue?.ToString()
                    : null;
            if (string.IsNullOrWhiteSpace(endpoint)
                || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                || (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrWhiteSpace(apiKey)
                || string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            connection = new AzureOpenAIReceiptConnection(endpoint, apiKey, model);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal sealed class AzureReceipt
    {
        [JsonPropertyName("subtotal")] public decimal Subtotal { get; init; }
        [JsonPropertyName("tax")] public decimal Tax { get; init; }
        [JsonPropertyName("tip")] public decimal Tip { get; init; }
        [JsonPropertyName("total")] public decimal Total { get; init; }
        [JsonPropertyName("discount")] public decimal Discount { get; init; }
        [JsonPropertyName("items")] public IReadOnlyList<AzureReceiptItem> Items { get; init; } = [];
    }

    internal sealed class AzureReceiptItem
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; init; }
        [JsonPropertyName("quantity")] public decimal Quantity { get; init; }
        [JsonPropertyName("totalPrice")] public decimal TotalPrice { get; init; }
        [JsonPropertyName("discountAmount")] public decimal DiscountAmount { get; init; }
        [JsonPropertyName("taxAmount")] public decimal TaxAmount { get; init; }
    }
}

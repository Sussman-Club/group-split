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
                instructions: "Extract a normalized receipt from the supplied document. Every "
                    + "line, every amount, and every word of a name comes from the receipt: "
                    + "expand the merchant's abbreviations into the words they stand for, and "
                    + "add nothing the receipt does not show.",
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
        Read the attached receipt and return the normalized receipt JSON requested by the schema.
        It may come from any merchant, a shop, a restaurant, a pharmacy, a fuel station, an online
        order, and it may be a photo, a scan, or a PDF that is skewed, creased, or faded. Read
        every page and every price column before you answer.

        Items. Include every positive, purchasable product, dish, or service line the merchant
        printed, in printed order. Never return discount, coupon, refund, payment, tender, change,
        loyalty, balance, subtotal, or total rows as items.

        Names. Receipt text is compressed to fit the paper: vowels are dropped, words run
        together, the merchant's own brand is initialised, sizes and units are stuck onto the end,
        and rows carry symbol prefixes or department and tax codes. Expand that text back into the
        words it stands for, and stop there. Every word you write has to be traceable to the
        printed line: to its letters, to a brand or department you can read elsewhere on the
        receipt, or to a unit or count printed beside it. Do not add a brand, flavour, variety,
        material, size, or count the line does not show, and do not name the specific product you
        believe the line refers to. When only part of a line decodes, expand that part and keep
        the rest as printed. When a line is a bare code with nothing to read into it, keep the
        printed text unchanged; an unexpanded name is always better than a guessed one. Strip
        leading and trailing symbols and department or tax codes. Write the name in the merchant's
        own language and do not translate it. Before you answer, read each name back against its
        printed line and delete any word that line does not support.

        Quantities. Use the printed quantity when present; otherwise use 1. Goods sold by weight,
        volume, or length keep their measured quantity, such as 0.734, with the matching per-unit
        price.

        Prices. unitPrice is the price of one unit as printed, before any discount. totalPrice is
        what the line actually cost, after its own discount and after its share of any
        receipt-wide discount. When only one of the two is printed, derive the other from the
        quantity.

        Discounts. Set discount to the total positive discount amount on the receipt, and set
        discountAmount on each item the receipt attributes a discount to. Allocate a receipt-wide
        discount across the items in proportion to their gross line totals, and put any rounding
        remainder on the largest item so the item totals still add up.

        Tax and tip. Report tax only when it is charged on top of the line prices. When the prices
        already include it and the tax block only restates what the total already contains, set
        tax and every taxAmount to 0. Otherwise put each line's own tax in taxAmount and the
        receipt-level tax in tax. Put service, cover, or gratuity charges in tip. Use 0 for an
        absent discount, tip, or tax rather than guessing.

        Totals. subtotal is the sum of the item totalPrice values, and total must equal subtotal
        plus tax plus tip. Check that total against the grand total the merchant printed. When
        they disagree, re-read the lines you are least certain about and correct the items; if
        they still disagree, keep the printed grand total in total. The item totals, subtotal, and
        total must all represent money that was actually charged.

        Numbers. Return plain decimals: no currency symbols, no thousands separators, and a dot
        for the decimal point. Read the merchant's own convention before you convert, because
        1.234,56 and 1,234.56 are both 1234.56.

        Return no prose outside the JSON object.
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
                [
                    .. receipt.Items.Select(item => new TranscribedReceiptItem(
                        item.Name,
                        item.UnitPrice,
                        item.Quantity,
                        item.TotalPrice,
                        item.TaxAmount))
                ]);
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
        connection = null!;
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

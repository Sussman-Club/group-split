using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Veryfi;

/// <summary>Veryfi's credentials and endpoint, kept at the provider boundary.</summary>
public sealed class VeryfiReceiptTranscriptionOptions
{
    public const string SectionName = "Veryfi";

    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Uri Endpoint { get; set; } = new("https://api.veryfi.com/");
}

/// <summary>Reads receipt documents with Veryfi's synchronous Process Document API.</summary>
public sealed class VeryfiReceiptTranscriptionProvider(
    HttpClient client,
    IOptions<VeryfiReceiptTranscriptionOptions> options,
    ILogger<VeryfiReceiptTranscriptionProvider> logger) : IReceiptTranscriptionProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "Veryfi";

    public async Task<TranscribedReceipt> Transcribe(ReceiptSourceDocument document, CancellationToken ct = default)
    {
        var settings = options.Value;
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(document.Content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(document.ContentType);
        form.Add(file, "file", document.FileName);
        form.Add(new StringContent($"receipt-attachment:{document.AttachmentId:N}"), "external_id");
        form.Add(new StringContent("receipt"), "document_type");
        // v8 leaves its dedicated line-item extraction mode off when this is omitted.
        form.Add(new StringContent("soft"), "thinking");
        // The response is all GroupSplit needs. Letting Veryfi delete its copy after that
        // avoids turning a transcription request into a second receipt archive.
        form.Add(new StringContent("true"), "auto_delete");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v8/partner/documents")
        {
            Content = form
        };
        request.Headers.Add("Client-Id", settings.ClientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("apikey", $"{settings.Username}:{settings.ApiKey}");
        // Repeated user-initiated transcriptions must be processed again instead of
        // replaying a previous (possibly empty) Veryfi result. The HTTP resilience
        // handler reuses this request and key for transport-level retries.
        request.Headers.Add("Idempotency-Key", $"receipt-transcription:{Guid.NewGuid():N}");

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Veryfi returned {StatusCode} while transcribing receipt attachment {AttachmentId}.",
                    (int)response.StatusCode, document.AttachmentId);
                throw new BadGatewayException(ErrorCodes.ReceiptTranscriptionProviderUnavailable,
                    "The receipt transcription service could not process this file. Please try again shortly.");
            }

            var parsed = await response.Content.ReadFromJsonAsync<VeryfiDocument>(Json, ct)
                ?? throw new JsonException("Veryfi returned an empty document response.");
            var receipt = parsed.ToReceipt();
            logger.LogInformation(
                "Veryfi returned {StructuredLineItemCount} structured line item(s) and mapped {MappedItemCount} positive named item(s) for receipt attachment {AttachmentId}.",
                parsed.LineItems?.Count ?? 0,
                receipt.Items.Count,
                document.AttachmentId);
            return receipt;
        }
        catch (BadGatewayException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            logger.LogWarning(error, "Veryfi could not transcribe receipt attachment {AttachmentId}.", document.AttachmentId);
            throw new BadGatewayException(ErrorCodes.ReceiptTranscriptionProviderUnavailable,
                "The receipt transcription service could not be reached. Please try again shortly.", error);
        }
    }

    private sealed class VeryfiDocument
    {
        [JsonPropertyName("subtotal")] public decimal? Subtotal { get; init; }
        [JsonPropertyName("tax")] public decimal? Tax { get; init; }
        [JsonPropertyName("tip")] public decimal? Tip { get; init; }
        [JsonPropertyName("total")] public decimal? Total { get; init; }
        [JsonPropertyName("line_items")] public IReadOnlyList<VeryfiLineItem>? LineItems { get; init; }

        public TranscribedReceipt ToReceipt()
        {
            var candidates = (LineItems ?? [])
                .Select(line => line.ToItem())
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();

            var subtotal = RoundMoney(Math.Max(0m, Subtotal ?? candidates.Sum(item => item.TotalPrice)));
            var tax = RoundMoney(Math.Max(0m, Tax ?? candidates.Sum(item => item.TaxAmount)));
            var tip = RoundMoney(Math.Max(0m, Tip ?? 0m));
            var total = RoundMoney(Math.Max(0m, Total ?? subtotal + tax + tip));

            // The receipt model represents purchased items, not coupons/refunds. Remove
            // zero and negative OCR rows above, then spread the receipt-level net subtotal
            // over the retained rows so the saved line totals still describe the bill.
            var items = ReconcileSubtotal(candidates, subtotal);
            var itemTaxes = items.Sum(item => item.TaxAmount) == tax
                ? items.Select(item => item.TaxAmount).ToArray()
                : Allocate(tax, items.Select(item => item.TotalPrice).ToArray());

            var mappedItems = items.Select((item, index) => new TranscribedReceiptItem(
                item.Name,
                Math.Max(0.01m, RoundMoney(item.TotalPrice / item.Quantity)),
                item.Quantity,
                item.TotalPrice,
                itemTaxes[index])
            {
                NormalizedName = item.NormalizedName,
                Description = item.Description
            }).ToArray();

            return new TranscribedReceipt(subtotal, tax, tip, total, mappedItems);
        }

        private static List<ReceiptItemDraft> ReconcileSubtotal(List<ReceiptItemDraft> candidates, decimal subtotal)
        {
            var retained = candidates;
            while (retained.Count > 0)
            {
                var totals = Allocate(subtotal, retained.Select(item => item.TotalPrice).ToArray());
                var withTotals = retained.Select((item, index) => item with { TotalPrice = totals[index] }).ToList();
                var positive = withTotals.Where(item => item.TotalPrice > 0m).ToList();
                if (positive.Count == retained.Count)
                    return positive;

                // Do not render zero-value rows as purchasable items. Re-run the allocation
                // over the remaining positive rows so their totals still equal the subtotal.
                retained = positive;
            }

            return [];
        }
    }

    private sealed class VeryfiLineItem
    {
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("full_description")] public string? FullDescription { get; init; }
        [JsonPropertyName("normalized_description")] public string? NormalizedDescription { get; init; }
        [JsonPropertyName("product_info")] public VeryfiProductInfo? ProductInfo { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("price")] public decimal? Price { get; init; }
        [JsonPropertyName("quantity")] public decimal? Quantity { get; init; }
        [JsonPropertyName("total")] public decimal? Total { get; init; }
        [JsonPropertyName("tax")] public decimal? Tax { get; init; }

        public ReceiptItemDraft? ToItem()
        {
            var name = new[] { ProductInfo?.ExpandedDescription, Description, FullDescription, Text }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var quantity = Quantity is > 0m
                ? Math.Round(Quantity.Value, 3, MidpointRounding.AwayFromZero)
                : 1m;
            if (quantity <= 0m)
                quantity = 1m;

            var total = RoundMoney(Total ?? (Price ?? 0m) * quantity);
            if (total <= 0m)
                return null;

            var tax = RoundMoney(Math.Max(0m, Tax ?? 0m));
            var normalizedName = string.IsNullOrWhiteSpace(NormalizedDescription)
                ? null
                : NormalizedDescription.Trim().ToLowerInvariant();
            return new ReceiptItemDraft(name, normalizedName, Description?.Trim(), quantity, total, tax);
        }
    }

    private sealed class VeryfiProductInfo
    {
        [JsonPropertyName("expanded_description")] public string? ExpandedDescription { get; init; }
    }

    private sealed record ReceiptItemDraft(
        string Name,
        string? NormalizedName,
        string? Description,
        decimal Quantity,
        decimal TotalPrice,
        decimal TaxAmount);

    private static decimal RoundMoney(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Splits a receipt-level amount by weights, assigning leftover cents deterministically.</summary>
    private static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0)
            return [];

        amount = RoundMoney(Math.Max(0m, amount));
        var totalWeight = weights.Sum(weight => Math.Max(0m, weight));
        if (totalWeight <= 0m)
            return new decimal[weights.Count];

        var targetCents = amount * 100m;
        var exactCents = weights.Select(weight => targetCents * Math.Max(0m, weight) / totalWeight).ToArray();
        var allocatedCents = exactCents.Select(decimal.Floor).ToArray();
        var remainingCents = (int)(targetCents - allocatedCents.Sum());

        foreach (var index in Enumerable.Range(0, weights.Count)
                     .OrderByDescending(index => exactCents[index] - allocatedCents[index])
                     .ThenBy(index => index)
                     .Take(remainingCents))
        {
            allocatedCents[index] += 1m;
        }

        return allocatedCents.Select(cents => cents / 100m).ToArray();
    }
}

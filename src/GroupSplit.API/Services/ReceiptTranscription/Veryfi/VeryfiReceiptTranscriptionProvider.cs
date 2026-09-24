using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GroupSplit.API.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.ReceiptTranscription.Veryfi;

/// <summary>Reads receipt documents with Veryfi's synchronous Process Document API.</summary>
public sealed partial class VeryfiReceiptTranscriptionProvider(
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

            // Read as text first so the raw body can be logged: what a receipt actually came
            // back as is the only way to tell a provider misreading from a mapping mistake.
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (settings.LogRawResponses)
                logger.LogInformation("Veryfi returned {Payload} for receipt attachment {AttachmentId}.",
                    payload, document.AttachmentId);

            var parsed = JsonSerializer.Deserialize<VeryfiDocument>(payload, Json)
                ?? throw new JsonException("Veryfi returned an empty document response.");
            var receipt = parsed.ToReceipt(payload);
            logger.LogInformation(
                "Veryfi returned {StructuredLineItemCount} structured line item(s) and mapped {MappedItemCount} named item(s) for receipt attachment {AttachmentId}.",
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
        [JsonPropertyName("tax")] public decimal? Tax { get; init; }
        [JsonPropertyName("tip")] public decimal? Tip { get; init; }
        [JsonPropertyName("total")] public decimal? Total { get; init; }
        [JsonPropertyName("vendor")] public JsonElement Vendor { get; init; }
        [JsonPropertyName("raw_vendor_name")] public string? RawVendorName { get; init; }
        [JsonPropertyName("date")] public string? Date { get; init; }
        [JsonPropertyName("line_items")] public IReadOnlyList<VeryfiLineItem>? LineItems { get; init; }

        public TranscribedReceipt ToReceipt(string payload)
        {
            var candidates = (LineItems ?? []).Select(line => line.ToRow()).OfType<ReceiptItemDraft>().ToList();
            // Never change a printed line to force a match with a receipt-level value.
            // A mismatch remains visible during review; the raw provider response retains
            // the original subtotal for comparison.
            var subtotal = candidates.Sum(item => item.TotalPrice);
            var tax = RoundMoney(Math.Max(0m, Tax ?? candidates.Sum(item => item.TaxAmount)));
            var tip = RoundMoney(Math.Max(0m, Tip ?? 0m));
            var total = RoundMoney(Math.Max(0m, Total ?? subtotal + tax + tip));

            var itemTaxes = candidates.Select(item => item.TaxAmount).ToArray();
            var remainingTax = tax - itemTaxes.Sum();
            var unknown = Enumerable.Range(0, candidates.Count)
                .Where(index => !candidates[index].TaxKnown &&
                    (remainingTax > 0 ? candidates[index].TotalPrice > 0 : candidates[index].TotalPrice < 0))
                .ToArray();
            var shares = Allocate(Math.Abs(remainingTax), unknown.Select(index => Math.Abs(candidates[index].TotalPrice)).ToArray());
            foreach (var (index, share) in unknown.Zip(shares))
                itemTaxes[index] += Math.Sign(remainingTax) * share;

            var mappedItems = candidates.Select((item, index) => new TranscribedReceiptItem(
                item.Name,
                item.UnitPrice ?? RoundMoney(item.TotalPrice / item.Quantity),
                item.Quantity,
                item.TotalPrice,
                itemTaxes[index])
            {
                NormalizedName = item.NormalizedName,
                Description = item.Description,
                TaxRate = item.TaxRate,
                IsTaxable = item.IsTaxable
            }).ToArray();

            string? vendor = Vendor.ValueKind switch
            {
                JsonValueKind.String => Vendor.GetString(),
                JsonValueKind.Object when Vendor.TryGetProperty("name", out var name) => name.GetString(),
                _ => null
            };
            return new TranscribedReceipt(subtotal, tax, tip, total, mappedItems)
            {
                MerchantName = Clean(vendor) ?? Clean(RawVendorName),
                PurchaseDate = PurchaseDateFrom(Date),
                SourceValues = Evidence(payload)
            };
        }

        /// <summary>
        /// The calendar day of Veryfi's <c>date</c>, which it sends as
        /// <c>yyyy-MM-dd HH:mm:ss</c>. Only the day is kept, read without the server's culture.
        /// </summary>
        private static DateOnly? PurchaseDateFrom(string? value) =>
            value?.Trim() is { Length: >= 10 } text &&
            DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
                ? date
                : null;

        /// <summary>
        /// The part of the response a reviewer needs to check the reading against the paper.
        /// The whole body also carries signed image links and payment details, and every
        /// member who can open the bill would see it, so only these fields are kept.
        /// </summary>
        private static string? Evidence(string payload)
        {
            if (JsonNode.Parse(payload) is not JsonObject source)
                return null;
            var evidence = new JsonObject();
            foreach (var key in EvidenceFields)
                if (source[key] is { } value)
                    evidence[key] = value.DeepClone();
            if (source["vendor"] is JsonObject vendor && vendor["name"] is { } vendorName)
                evidence["vendor"] = new JsonObject { ["name"] = vendorName.DeepClone() };

            var text = evidence.ToJsonString();
            if (text.Length > SaveReceiptRequest.SourceValuesMaxLength)
            {
                // The OCR text is the bulk of a long receipt and the structured lines above
                // already say what it was read as.
                evidence.Remove("ocr_text");
                text = evidence.ToJsonString();
            }
            return text.Length > SaveReceiptRequest.SourceValuesMaxLength
                ? text[..SaveReceiptRequest.SourceValuesMaxLength]
                : text;
        }

        private static readonly string[] EvidenceFields =
            ["raw_vendor_name", "date", "subtotal", "tax", "tip", "total", "line_items", "ocr_text"];

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class VeryfiLineItem
    {
        /// <summary>Veryfi's own words for rows that take money off rather than put it on.</summary>
        private static readonly HashSet<string> ReductionTypes =
            new(StringComparer.OrdinalIgnoreCase) { "discount", "refund" };

        /// <summary>
        /// Rows that are no part of what the items come to. Anything unrecognised stays an item:
        /// a service or delivery charge does belong on the bill, and a purchase must not go
        /// missing because the provider named its type something this list has not heard of.
        /// </summary>
        private static readonly HashSet<string> NonItemTypes =
            new(StringComparer.OrdinalIgnoreCase) { "tax", "payment" };

        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("full_description")] public string? FullDescription { get; init; }
        [JsonPropertyName("normalized_description")] public string? NormalizedDescription { get; init; }
        [JsonPropertyName("product_info")] public VeryfiProductInfo? ProductInfo { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("sku")] public string? Sku { get; init; }
        [JsonPropertyName("price")] public decimal? Price { get; init; }
        [JsonPropertyName("quantity")] public decimal? Quantity { get; init; }
        [JsonPropertyName("total")] public decimal? Total { get; init; }
        [JsonPropertyName("discount")] public decimal? Discount { get; init; }
        [JsonPropertyName("tax")] public decimal? Tax { get; init; }
        [JsonPropertyName("tax_rate")] public decimal? TaxRate { get; init; }

        public ReceiptItemDraft? ToRow()
        {
            var type = Type?.Trim();
            if (!string.IsNullOrEmpty(type) && NonItemTypes.Contains(type))
                return null;

            var quantity = Quantity is > 0m ? Math.Round(Quantity.Value, 3, MidpointRounding.AwayFromZero) : 0m;
            var total = RoundMoney(Total ?? (Price ?? 0m) * (quantity > 0m ? quantity : 1m));
            var named = new[] { ProductInfo?.ExpandedDescription, Description, FullDescription }
                .Any(value => !string.IsNullOrWhiteSpace(value));

            // A reduction is an ordinary signed line. Some provider rows borrow a nearby
            // purchase's positive total; their explicit discount amount is the reliable
            // amount to keep, not another thing the group bought.
            // Veryfi signs the discount either way round. Whichever it is, the explicit
            // saving outranks a total that may have borrowed a neighbouring purchase price.
            var reduced = RoundMoney(Math.Abs(Discount ?? 0m));
            if ((!string.IsNullOrEmpty(type) && ReductionTypes.Contains(type))
                || total < 0m
                || (reduced > 0m && !named))
            {
                var amount = reduced > 0m ? reduced : Math.Abs(total);
                // A reduction row that takes nothing off is not a line anybody can split.
                if (amount == 0m)
                    return null;
                var label = CleanLabel(Description, FullDescription, Text)
                    ?? (string.IsNullOrWhiteSpace(Sku) ? "Discount" : $"Coupon {Sku.Trim()}");
                if (label.Length > 128) label = label[..128].TrimEnd();
                return new ReceiptItemDraft(label, label.ToLowerInvariant(), Text?.Trim(),
                    1m, -amount, -amount, RoundMoney(Tax ?? 0m), Tax.HasValue, TaxRate,
                    TaxRate is { } couponRate ? couponRate > 0m : null);
            }

            var name = new[] { ProductInfo?.ExpandedDescription, Description, FullDescription }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                ?? NameFromText(Text, Sku);
            if (string.IsNullOrWhiteSpace(name))
                return null;
            // A bill refuses a name longer than this, and a row whose name came off its raw
            // text can run long. Better a clipped name than a draft that will not save.
            if (name.Length > 128)
                name = name[..128].TrimEnd();

            var (count, unitPrice) = ReadQuantity(Price, quantity, total);
            var tax = RoundMoney(Math.Max(0m, Tax ?? 0m));
            var normalizedName = string.IsNullOrWhiteSpace(NormalizedDescription)
                ? null
                : NormalizedDescription.Trim().ToLowerInvariant();
            var taxable = TaxRate is { } rate ? rate > 0m
                : Text is { } text && Regex.IsMatch(text, @"\sN\s*$", RegexOptions.IgnoreCase) ? false
                : (bool?)null;
            return new ReceiptItemDraft(name, normalizedName, Description?.Trim(),
                count, unitPrice, total, tax, Tax.HasValue, TaxRate, taxable);
        }

        /// <summary>
        /// Quantity, price per unit and line total are read off the paper separately, and a
        /// receipt that prints its item number where a quantity would go makes one of them a
        /// misreading -- Costco's "8 2% MILK 1GAL 2.99" is one jug of milk, not eight. The three
        /// figures owe each other price * quantity == total, so when they disagree and both the
        /// total and the unit price are legible, the quantity is the one to work out again.
        /// </summary>
        private static (decimal Quantity, decimal? UnitPrice) ReadQuantity(
            decimal? price, decimal quantity, decimal total)
        {
            var unit = price is > 0m ? RoundMoney(price.Value) : (decimal?)null;
            if (unit is null)
                return (quantity > 0m ? quantity : 1m, null);

            if (quantity > 0m && RoundMoney(unit.Value * quantity) == total)
                return (quantity, unit);

            // A whole number of them at that price is a reading that makes sense of all three.
            // A fraction is not: dividing one misread figure by another always lands somewhere,
            // and 7.79 against a price of 3.99 gives 1.952 packs of mozzarella, which is an
            // arithmetic result rather than a shopping one.
            var implied = Math.Round(total / unit.Value, 3, MidpointRounding.AwayFromZero);
            if (implied > 0m && implied == Math.Truncate(implied) && RoundMoney(unit.Value * implied) == total)
                return (implied, unit);

            // Nothing reconciles. The total is what the bill adds itself up from, so it stands
            // alone: one of whatever this is, at what it cost.
            return (1m, null);
        }

        /// <summary>
        /// The last resort when nothing on the row named what was bought. The raw text carries
        /// the item number in front of the name and the price behind it, and "568915\t\t5.29 N"
        /// is not what to call a cucumber, so those come off and the words left over are the
        /// name. A row that turns out to be all numbers -- the name never made it off the paper
        /// at all -- is called after its item number, which can at least be found on the bill.
        /// </summary>
        private static string? NameFromText(string? text, string? sku)
        {
            var lines = (text ?? string.Empty)
                .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var stripped = TrailingAmount().Replace(LeadingItemNumber().Replace(line, string.Empty), string.Empty);
                var tidied = Whitespace().Replace(stripped, " ").Trim();
                if (tidied.Any(char.IsLetter))
                    return tidied;
            }

            return Tokens(sku, text).FirstOrDefault() is { } number ? $"Item {number}" : null;
        }

        private static string? CleanLabel(params string?[] values) => values
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        /// <summary>
        /// Item numbers used when the printed line has no readable product name. Three
        /// digits and up keeps a quantity or small price from becoming a fallback name.
        /// </summary>
        private static IReadOnlyList<string> Tokens(params string?[] values) => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => SkuLikeToken().Matches(value!).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    [GeneratedRegex(@"\d{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex SkuLikeToken();

    /// <summary>The item number a printed line opens with, before the name.</summary>
    [GeneratedRegex(@"^\s*\d{3,}\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingItemNumber();

    /// <summary>What a printed line ends with: the money, and the single-letter tax flag.</summary>
    [GeneratedRegex(@"\s*-?\d+[.,]\d{2}-?\s*[A-Za-z]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingAmount();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private sealed class VeryfiProductInfo
    {
        [JsonPropertyName("expanded_description")] public string? ExpandedDescription { get; init; }
    }

    /// <summary>
    /// A line on its way to becoming a bill item, with its printed total intact.
    /// </summary>
    private sealed record ReceiptItemDraft(
        string Name,
        string? NormalizedName,
        string? Description,
        decimal Quantity,
        decimal? UnitPrice,
        decimal TotalPrice,
        decimal TaxAmount,
        bool TaxKnown,
        decimal? TaxRate,
        bool? IsTaxable);

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

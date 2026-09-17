using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GroupSplit.API.Errors;
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
            var (receipt, pricesAsPrinted) = parsed.ToReceipt();
            logger.LogInformation(
                "Veryfi returned {StructuredLineItemCount} structured line item(s) and mapped {MappedItemCount} positive named item(s) for receipt attachment {AttachmentId}; line prices {PriceOutcome}.",
                parsed.LineItems?.Count ?? 0,
                receipt.Items.Count,
                document.AttachmentId,
                pricesAsPrinted ? "kept as printed" : "scaled to the receipt subtotal");
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

        public (TranscribedReceipt Receipt, bool PricesAsPrinted) ToReceipt()
        {
            var rows = (LineItems ?? []).Select(line => line.ToRow()).OfType<VeryfiRow>().ToList();
            var candidates = rows.Select(row => row.Item).OfType<ReceiptItemDraft>().ToList();
            var reductions = rows.Select(row => row.Reduction).OfType<ReceiptReduction>().ToList();

            var gross = candidates.Sum(item => item.TotalPrice);
            var subtotal = RoundMoney(Math.Max(0m, Subtotal ?? gross - reductions.Sum(reduction => reduction.Amount)));
            var tax = RoundMoney(Math.Max(0m, Tax ?? candidates.Sum(item => item.TaxAmount)));
            var tip = RoundMoney(Math.Max(0m, Tip ?? 0m));
            var total = RoundMoney(Math.Max(0m, Total ?? subtotal + tax + tip));

            // A coupon names the line it comes off, so taking it off that one line leaves every
            // other line at the price the bill printed. Only when the coupons cannot be placed
            // does the receipt-level subtotal get spread across the rows instead, which moves
            // every price on the bill and is a last resort rather than the ordinary reading.
            var attributed = Attribute(candidates, reductions, subtotal);
            var items = attributed ?? ReconcileSubtotal(candidates, subtotal);
            var itemTaxes = items.Sum(item => item.TaxAmount) == tax
                ? items.Select(item => item.TaxAmount).ToArray()
                : Allocate(tax, items.Select(item => item.TotalPrice).ToArray());

            var mappedItems = items.Select((item, index) => new TranscribedReceiptItem(
                item.Name,
                UnitPriceFor(item),
                item.Quantity,
                item.TotalPrice,
                itemTaxes[index])
            {
                NormalizedName = item.NormalizedName,
                Description = item.Description
            }).ToArray();

            return (new TranscribedReceipt(subtotal, tax, tip, total, mappedItems),
                items.All(item => item.TotalPrice == item.PrintedTotal));
        }

        /// <summary>
        /// The price per unit Veryfi read, for as long as the line still carries the total that
        /// price was read against. A line whose total has moved -- a coupon taken off it, or the
        /// subtotal spread over it -- no longer costs what the paper said, so its unit price is
        /// worked out from what the line now comes to.
        /// </summary>
        private static decimal UnitPriceFor(ReceiptItemDraft item) =>
            item.TotalPrice == item.PrintedTotal && item.UnitPrice is { } printed
                ? printed
                : Math.Max(0.01m, RoundMoney(item.TotalPrice / item.Quantity));

        /// <summary>
        /// Takes each reduction off the single line it names. All or nothing, and only when the
        /// result lands exactly on the receipt's own subtotal: a partly attributed bill would be
        /// a set of prices read two different ways off one piece of paper.
        /// </summary>
        private static List<ReceiptItemDraft>? Attribute(
            List<ReceiptItemDraft> items, List<ReceiptReduction> reductions, decimal subtotal)
        {
            if (reductions.Count == 0 || items.Count == 0)
                return null;

            var totals = items.Select(item => item.TotalPrice).ToArray();
            foreach (var reduction in reductions)
            {
                var named = Enumerable.Range(0, items.Count)
                    .Where(index => items[index].Skus.Any(reduction.Names.Contains))
                    .ToArray();
                // Nothing named, or more than one line answering to the same number, is a coupon
                // this cannot place. Guessing at one would take money off the wrong line.
                if (named.Length != 1 || totals[named[0]] - reduction.Amount <= 0m)
                    return null;
                totals[named[0]] -= reduction.Amount;
            }

            return totals.Sum() == subtotal
                ? items.Select((item, index) => item with { TotalPrice = totals[index] }).ToList()
                : null;
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

        public VeryfiRow? ToRow()
        {
            var type = Type?.Trim();
            if (!string.IsNullOrEmpty(type) && NonItemTypes.Contains(type))
                return null;

            var quantity = Quantity is > 0m ? Math.Round(Quantity.Value, 3, MidpointRounding.AwayFromZero) : 0m;
            var total = RoundMoney(Total ?? (Price ?? 0m) * (quantity > 0m ? quantity : 1m));
            var named = new[] { ProductInfo?.ExpandedDescription, Description, FullDescription }
                .Any(value => !string.IsNullOrWhiteSpace(value));

            // A row that takes money off and names nothing bought is a coupon, whatever its
            // total came out as. Costco prints its coupons in a column of their own, and a
            // provider that reads a neighbouring "2 @ 14.99" into the same row hands back a
            // positive total for what is still a coupon -- 27.28 for a 2.70 saving. Believing
            // that total would put a thing nobody bought on the bill and lose the saving.
            var reduced = RoundMoney(Math.Max(0m, -(Discount ?? 0m)));
            if ((!string.IsNullOrEmpty(type) && ReductionTypes.Contains(type))
                || total < 0m
                || (reduced > 0m && !named))
            {
                var amount = reduced > 0m ? reduced : -total;
                return amount > 0m
                    ? new VeryfiRow(null, new ReceiptReduction(amount, Tokens(Sku, Text, Description, FullDescription)))
                    : null;
            }

            var name = new[] { ProductInfo?.ExpandedDescription, Description, FullDescription }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                ?? NameFromText(Text, Sku);
            if (string.IsNullOrWhiteSpace(name) || total <= 0m)
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
            var skus = Tokens(Sku);
            if (skus.Count == 0)
                skus = Tokens(Text);

            return new VeryfiRow(new ReceiptItemDraft(name, normalizedName, Description?.Trim(), skus,
                count, unitPrice, total, total, tax), null);
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

        /// <summary>
        /// The item numbers a row mentions. Costco prints a coupon as "388175 / 1776788", the
        /// second being the number of the line it comes off. Three digits and up, so that a
        /// quantity or a price under a hundred is never taken for one.
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

    /// <summary>One parsed Veryfi row: something bought, or an amount that comes off the bill.</summary>
    private sealed record VeryfiRow(ReceiptItemDraft? Item, ReceiptReduction? Reduction);

    /// <summary>An amount to take off, and the item numbers the row named while doing so.</summary>
    private sealed record ReceiptReduction(decimal Amount, IReadOnlyList<string> Names);

    /// <summary>
    /// A line on its way to becoming a bill item. <see cref="PrintedTotal"/> is what the paper
    /// said and never moves, so a line whose total has since been changed can be told apart
    /// from one still standing at the price it was read at.
    /// </summary>
    private sealed record ReceiptItemDraft(
        string Name,
        string? NormalizedName,
        string? Description,
        IReadOnlyList<string> Skus,
        decimal Quantity,
        decimal? UnitPrice,
        decimal PrintedTotal,
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

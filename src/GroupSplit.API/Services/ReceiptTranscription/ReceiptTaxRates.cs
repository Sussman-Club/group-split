namespace GroupSplit.API.Services.ReceiptTranscription;

internal static class ReceiptTaxRates
{
    // Providers sometimes return a ratio (0.075) although the contract uses percent (7.5).
    // Keep a genuine sub-one-percent rate when the printed line tax supports it.
    public static decimal? Normalize(decimal? rate, decimal price, decimal tax)
    {
        if (rate is not { } value || value < 0 || value > 100)
            return null;
        if (value is > 0 and < 1)
        {
            var percentTax = decimal.Round(price * value / 100m, 2, MidpointRounding.AwayFromZero);
            var ratioTax = decimal.Round(price * value, 2, MidpointRounding.AwayFromZero);
            if (tax != 0 && Math.Abs(tax - ratioTax) < Math.Abs(tax - percentTax))
                value *= 100;
        }
        return decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    }

    public static TranscribedReceiptItem[] Normalize(decimal totalTax, IReadOnlyList<TranscribedReceiptItem> items)
    {
        var percentages = items.Select(item => item with
        {
            TaxRate = Normalize(item.TaxRate, item.TotalPrice, item.TaxAmount)
        }).ToArray();
        var ratios = percentages.Select(item => item.TaxAmount == 0 && item.IsTaxable != false
            && item.TaxRate is > 0 and < 1 ? item with { TaxRate = item.TaxRate * 100m } : item).ToArray();

        // Missing line tax is not evidence of ratio units. Only use the receipt total
        // when every non-exempt line is accounted for and the ratio interpretation
        // reconciles it within ordinary per-line cent rounding. Ties keep percent units.
        if (items.Any(item => item.IsTaxable != false && item.TotalPrice != 0
            && item.TaxRate is null && item.TaxAmount == 0))
            return percentages;
        static decimal SumTax(IEnumerable<TranscribedReceiptItem> lines) => lines.Sum(item =>
            item.IsTaxable == false ? 0m : item.TaxRate is { } rate
                ? decimal.Round(item.TotalPrice * rate / 100m, 2, MidpointRounding.AwayFromZero)
                : item.TaxAmount);
        var ratioError = Math.Abs(totalTax - SumTax(ratios));
        return ratioError <= .005m * items.Count && ratioError < Math.Abs(totalTax - SumTax(percentages))
            ? ratios : percentages;
    }
}

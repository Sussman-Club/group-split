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
            if (tax == 0 || Math.Abs(tax - ratioTax) < Math.Abs(tax - percentTax))
                value *= 100;
        }
        return decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    }
}

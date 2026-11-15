namespace GroupSplit.Shared;

/// <summary>Minor-unit precision for monetary arithmetic, independent of display culture.</summary>
public static class CurrencyPrecision
{
    // ISO 4217 active currencies and funds with 0–3 minor-unit digits, SIX list one,
    // published 2026-09-17. Four-digit funds and non-monetary codes are not supported.
    // https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml
    private static readonly HashSet<string> Supported = new(
        ("BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX UYI VND VUV XAF XOF XPF " +
         "AED AFN ALL AMD AOA ARS AUD AWG AZN BAM BBD BDT BMD BND BOB BOV BRL BSD BTN BWP BYN BZD " +
         "CAD CDF CHE CHF CHW CNY COP COU CRC CUP CVE CZK DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP " +
         "GEL GHS GIP GMD GTQ GYD HKD HNL HTG HUF IDR ILS INR IRR JMD KES KGS KHR KPW KYD KZT " +
         "LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MXV MYR MZN " +
         "NAD NGN NIO NOK NPR NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK " +
         "SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS TMT TOP TRY TTD TWD TZS UAH USD USN " +
         "UYU UZS VED VES WST XAD XCD XCG YER ZAR ZMW ZWG BHD IQD JOD KWD LYD OMR TND")
        .Split(' '), StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string? currency) => currency is not null && Supported.Contains(currency.Trim());

    /// <summary>Supported currency codes, with common choices first.</summary>
    public static IReadOnlyList<string> Codes { get; } =
        [.. new[] { "USD", "EUR", "GBP", "CAD", "AUD", "JPY", "KWD" }
            .Concat(Supported.Order(StringComparer.Ordinal).Except(["USD", "EUR", "GBP", "CAD", "AUD", "JPY", "KWD"]))];

    public static int DecimalPlaces(string? currency) => currency?.Trim().ToUpperInvariant() switch
    {
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF" or
        "UGX" or "UYI" or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        _ => 2
    };

    public static bool IsValidAmount(decimal amount, string? currency) =>
        decimal.Round(amount, DecimalPlaces(currency)) == amount;
}

using System.Globalization;

namespace GroupSplit.App.Shared.Extensions;

public static class MoneyExtensions
{
    /// <summary>
    /// The symbols worth spelling out. Anything absent is shown as its ISO code, which is
    /// unambiguous and never wrong -- unlike guessing a symbol several currencies share.
    /// </summary>
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["JPY"] = "¥",
        ["CAD"] = "CA$",
        ["AUD"] = "A$"
    };

    // Legacy amounts are USD. Language changes separators and placement, never currency.
    public static string ToMoney(this decimal amount) => amount.ToMoney("USD");

    /// <summary>
    /// What to put in front of an amount field for this currency: its symbol where it has
    /// one worth spelling out, and its code otherwise.
    /// </summary>
    public static string CurrencySymbol(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return Symbols["USD"];

        var code = currency.Trim().ToUpperInvariant();

        return Symbols.TryGetValue(code, out var symbol) ? symbol : code;
    }

    /// <summary>
    /// The currencies the expense dialog offers for a personal expense: the ones with a
    /// symbol here, which covers what anybody using this has spent in so far.
    /// </summary>
    /// <remarks>
    /// A short list rather than all of ISO 4217, because a select of 180 codes is a worse
    /// way to find the euro. The API takes any three-letter code, and so does the CLI, so a
    /// currency missing here is a line to add rather than something nobody can record.
    /// </remarks>
    public static IReadOnlyList<string> OfferedCurrencies { get; } = ["USD", "EUR", "GBP", "CAD", "AUD", "JPY"];

    /// <summary>
    /// Formats an amount that knows which currency it is in.
    /// </summary>
    /// <remarks>
    /// Imported bank rows carry their own currency, and a euro charge shown with a dollar
    /// sign is not a formatting quibble: it is a different number. Only the number format
    /// follows the reader's culture; the supplied currency always wins.
    /// </remarks>
    public static string ToMoney(this decimal amount, string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return amount.ToMoney();

        var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        var code = currency.Trim().ToUpperInvariant();

        format.CurrencySymbol = Symbols.TryGetValue(code, out var symbol) ? symbol : code + " ";

        return amount.ToString("C", format);
    }
}

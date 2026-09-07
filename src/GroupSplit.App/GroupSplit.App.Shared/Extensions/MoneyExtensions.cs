using System.Globalization;

namespace GroupSplit.App.Shared.Extensions;

public static class MoneyExtensions
{
    // Amounts carry no currency of their own, so "C" would follow whichever
    // culture happens to render: the server's during prerender, then the
    // browser's language once the WebAssembly runtime takes over. A British
    // browser would flip every figure from $ to £ on hydration. Pinned until
    // the data model carries a currency.
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

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

    public static string ToMoney(this decimal amount) => amount.ToString("C", Culture);

    /// <summary>
    /// Formats an amount that knows which currency it is in.
    /// </summary>
    /// <remarks>
    /// Imported bank rows carry their own currency, and a euro charge shown with a dollar
    /// sign is not a formatting quibble: it is a different number. The layout stays the
    /// pinned one, so only the symbol moves.
    /// </remarks>
    public static string ToMoney(this decimal amount, string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return amount.ToMoney();

        var format = (NumberFormatInfo)Culture.NumberFormat.Clone();
        var code = currency.Trim().ToUpperInvariant();

        format.CurrencySymbol = Symbols.TryGetValue(code, out var symbol) ? symbol : code + " ";

        return amount.ToString("C", format);
    }
}

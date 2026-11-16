using System.Globalization;
using GroupSplit.Shared;

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
        {
            return Symbols["USD"];
        }

        var code = currency.Trim().ToUpperInvariant();

        return Symbols.TryGetValue(code, out var symbol) ? symbol : code;
    }

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
        {
            return amount.ToMoney();
        }

        var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        var code = currency.Trim().ToUpperInvariant();

        format.CurrencySymbol = Symbols.TryGetValue(code, out var symbol) ? symbol : code + " ";
        format.CurrencyDecimalDigits = CurrencyPrecision.DecimalPlaces(code);

        return amount.ToString("C", format);
    }
}

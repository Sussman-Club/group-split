using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GroupSplit.Shared;

/// <summary>
/// Reads a number a person typed: an amount, a share, a percentage, a quantity. The one
/// definition both clients read with, so the app and the CLI cannot disagree about what
/// <c>12,50</c> means.
/// </summary>
/// <remarks>
/// Written by hand rather than left to <see cref="decimal.TryParse(string, NumberStyles, IFormatProvider, out decimal)"/>,
/// because every culture-driven parse reads some separator as a thousands separator and
/// drops it. That is how <c>12,50</c> was stored as 1250 (#313): invariant and en-US read
/// the comma as grouping, es-ES reads the dot that way, and nothing on the path refused
/// it. An amount here is never written with grouping, so a separator is only ever the
/// decimal point -- and anything that could be read two ways is refused rather than
/// guessed at.
/// <list type="bullet">
/// <item>Digits, an optional leading <c>-</c>, and at most one decimal separator.</item>
/// <item>No thousands separators of any kind: <c>1,234.56</c> and <c>1.234,56</c> are refused.</item>
/// <item>No more decimal places than the field holds: <c>12.555</c> is refused for money,
/// not rounded to something nobody typed.</item>
/// <item>A comma is the decimal point only where the caller allows it. The app does, because
/// a phone keypad in many locales offers nothing else; the CLI does not, so a script means
/// the same thing on every machine.</item>
/// </list>
/// </remarks>
public static class DecimalText
{
    /// <summary>Money, a share and a percentage all hold hundredths.</summary>
    public const int MoneyPlaces = 2;

    /// <param name="text">What was typed. Surrounding whitespace is ignored.</param>
    /// <param name="places">The most decimal places the value may have.</param>
    /// <param name="commaIsDecimal">Whether <c>,</c> may stand for the decimal point.</param>
    /// <param name="value">The number, when it could be read.</param>
    /// <param name="problem">
    /// Why it could not be, as a sentence that can be shown on its own beside a field or
    /// after the value in an error.
    /// </param>
    public static bool TryParse(
        string? text,
        int places,
        bool commaIsDecimal,
        out decimal value,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(places);

        value = 0m;
        var trimmed = text?.Trim() ?? string.Empty;
        var digits = trimmed.StartsWith('-') ? trimmed[1..] : trimmed;

        // ASCII digits only: char.IsDigit would let through Arabic-Indic and full-width
        // digits, which the invariant parse below then refuses as something else.
        if (!digits.Any(c => c is >= '0' and <= '9')
            || digits.Any(c => c is not (>= '0' and <= '9' or '.' or ',')))
        {
            problem = "Not a number.";
            return false;
        }

        var dots = digits.Count(c => c == '.');
        var commas = digits.Count(c => c == ',');

        if (commas > 0 && !commaIsDecimal)
        {
            problem = "Use '.' for the decimal point and no thousands separators, as in 1234.56.";
            return false;
        }

        if (dots + commas > 1)
        {
            problem = commaIsDecimal
                ? "Use one decimal separator and no thousands separators, as in 1234.56."
                : "Use '.' for the decimal point and no thousands separators, as in 1234.56.";
            return false;
        }

        var separator = digits.IndexOfAny(['.', ',']);

        if (separator >= 0 && digits.Length - separator - 1 > places)
        {
            problem = places == 0
                ? "Use a whole number."
                : $"Use at most {places} decimal place{(places == 1 ? "" : "s")}.";
            return false;
        }

        var normalized = (trimmed.StartsWith('-') ? "-" : "") + digits.Replace(',', '.');

        if (!decimal.TryParse(normalized,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value))
        {
            problem = "Too large.";
            return false;
        }

        problem = null;
        return true;
    }
}

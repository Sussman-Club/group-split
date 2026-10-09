using System.CommandLine;
using System.CommandLine.Parsing;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Every amount argument and option on the command line, read in one documented format:
/// digits with a <c>.</c> for the decimal point, no thousands separators, at most two
/// decimal places. <c>12.50</c>, never <c>12,50</c> or <c>1,234.56</c>.
/// </summary>
/// <remarks>
/// System.CommandLine reads a <see cref="decimal"/> with the machine's culture and accepts
/// grouping, so the same script meant two different things on two laptops: on en-US
/// <c>12,50</c> was 1250, and on es-ES <c>12.50</c> was (#313). An amount is now read the
/// same way everywhere, and anything that is not in that one format is a usage error -- exit
/// code 3, before any request -- rather than a number a hundred times too large.
/// </remarks>
public static class Amounts
{
    /// <summary>A required amount argument, such as the total of <c>transactions create</c>.</summary>
    public static Argument<decimal> Argument(string name, string description) =>
        new(name) { Description = description, CustomParser = result => Read(result) ?? 0m };

    /// <summary>An amount option whose absence means "not given".</summary>
    public static Option<decimal?> Option(string name, string description) =>
        new(name) { Description = description, CustomParser = Read };

    /// <summary>An amount option whose absence means zero, such as a bill's tip.</summary>
    public static Option<decimal> OptionOrZero(string name, string description) =>
        new(name) { Description = description, CustomParser = result => Read(result) ?? 0m };

    /// <summary>
    /// Reads one amount typed somewhere other than its own argument -- inside an
    /// <c>id=value</c> pair or a receipt line -- or says why it could not.
    /// </summary>
    public static bool TryRead(string text, int places, out decimal value, out string problem)
    {
        if (DecimalText.TryParse(text, places, commaIsDecimal: false, out value, out var why))
        {
            problem = string.Empty;
            return true;
        }

        problem = why;
        return false;
    }

    private static decimal? Read(ArgumentResult result)
    {
        // Arity has already refused a missing value; this only ever sees the one token.
        if (result.Tokens is not [var token])
            return null;

        if (TryRead(token.Value, DecimalText.MoneyPlaces, out var value, out var problem))
            return value;

        var name = result.Parent is OptionResult option ? option.Option.Name : result.Argument.Name;

        result.AddError($"Could not read {name} '{token.Value}'. {problem}");
        return null;
    }
}

using System.Globalization;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>Reads [item-id#]name=price[xqty][/taxamount][@rule-version-id].</summary>
public static class ReceiptItems
{
    private const string Sample = "Wine=18.00";

    public static IReadOnlyList<ReceiptItemInput> Parse(string option, IEnumerable<string> values)
    {
        var items = new List<ReceiptItemInput>();

        foreach (var value in values)
            items.Add(One(option, value));

        return items;
    }

    private static ReceiptItemInput One(string option, string value)
    {
        var separator = value.IndexOf('=');

        if (separator <= 0)
            throw Invalid(option, value, "it needs the form <name>=<price>");

        var name = value[..separator].Trim();

        if (name.Length == 0)
            throw Invalid(option, value, "the part before '=' is empty");

        // Which stored line this is, for a bill being corrected rather than written. Without
        // it every save was a bill the server had never seen: the API matches lines by the id
        // the client sends back, and nothing here ever sent one -- so every stored line was
        // dropped and re-created, and fixing one mistyped price silently cleared the rule
        // off every line. The expense then could not be divided, and nothing said why.
        //
        // Read only when the text before '#' is a guid, so a line genuinely called
        // "Table #4" is still a line called "Table #4".
        Guid? id = null;

        if (name.IndexOf('#') is var hash and > 0
            && Guid.TryParse(name[..hash].Trim(), out var stored))
        {
            id = stored;
            name = name[(hash + 1)..].Trim();

            if (name.Length == 0)
                throw Invalid(option, value, "there is an id but no name after it");
        }

        var rest = value[(separator + 1)..].Trim();
        Guid? ruleVersionId = null;
        var tax = 0m;

        // Lifted out before anything else is read, and spliced rather than truncated, so it
        // can be written on either side of the rule -- "1.99/notax@<id>" and
        // "1.99@<id>/notax" are the same line. Nothing else in a line contains a '/'.
        if (rest.IndexOf('/') is var slash and >= 0)
        {
            var after = rest[(slash + 1)..];
            var ends = after.IndexOf('@');
            var written = (ends >= 0 ? after[..ends] : after).Trim();

            var flag = written.ToLowerInvariant();

            if (flag == "notax")
            {
                // Still accepted and still true: this line carried none of the bill's tax. It
                // is now also the default, so it says nothing a bare line does not -- worth
                // keeping, because on a warehouse bill the exempt lines are exactly the ones
                // somebody is deliberately marking.
                tax = 0m;
            }
            else if (flag.StartsWith("tax", StringComparison.Ordinal))
            {
                var amount = flag[3..].Trim();

                if (amount.Length == 0)
                {
                    throw Invalid(option, value,
                        "'/tax' needs the amount charged on this line, as in '/tax8.05'. A " +
                        "bill can charge two rates, so which lines were taxed no longer says " +
                        "how much each of them carried");
                }

                tax = Number(option, value, amount, "a tax amount");
            }
            else
            {
                throw Invalid(option, value,
                    $"'{written}' is not a flag; the only ones are 'notax' and 'tax<amount>'");
            }

            rest = (rest[..slash] + (ends >= 0 ? after[ends..] : string.Empty)).Trim();
        }

        // The rule last, so a name may contain anything but '=' and the price may contain
        // anything but '@'.
        if (rest.IndexOf('@') is var at and >= 0)
        {
            var who = rest[(at + 1)..].Trim();
            rest = rest[..at].Trim();

            if (!Guid.TryParse(who, out var versionId))
                throw Invalid(option, value, "the value after @ must be one split rule version id");
            ruleVersionId = versionId;
        }

        var quantity = 1m;

        // The quantity rides on the price rather than taking an option of its own, because
        // an option cannot repeat per line -- there is one --item per line and the whole
        // line has to fit in it.
        if (rest.IndexOf('x', StringComparison.OrdinalIgnoreCase) is var times and >= 0)
        {
            quantity = Number(option, value, rest[(times + 1)..], "a quantity");
            rest = rest[..times].Trim();
        }

        var totalPrice = Number(option, value, rest, "a price");

        return new ReceiptItemInput
        {
            Id = id,
            Name = name,
            TotalPrice = totalPrice,
            Quantity = quantity,
            TaxAmount = tax,
            // What one of them cost, which is the line divided by how many -- and the line
            // itself when that is one, which is nearly always. Rounded because it is shown
            // and never divided by: the division reads TotalPrice, exactly as the bill does.
            UnitPrice = quantity == 0 ? totalPrice : decimal.Round(totalPrice / quantity, 2),
            SplitRuleVersionId = ruleVersionId
        };
    }

    private static decimal Number(string option, string value, string text, string what)
        => decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Invalid(option, value, $"'{text.Trim()}' is not {what}");

    private static CliException Invalid(string option, string value, string why)
        => CliException.Input(
            $"Could not read {option} '{value}': {why}.",
            $"Use {option} <name>=<price>[x<qty>][/tax<amount>][@<rule-version-id>], "
            + $"e.g. {option} {Sample} or "
            + $"{option} Wine=18.00@3f25c1a8-1111-2222-3333-444455556666");
}

using System.Globalization;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// A line of a bill written on one line of a shell: <c>Wine=18.00x2@alice,bob*2</c>.
/// </summary>
/// <remarks>
/// Denser than the <c>id=value</c> pairs elsewhere, because a line carries four things and
/// typing a whole receipt is the point. Read left to right: what it was called, what the
/// line came to, optionally how many, optionally who had it.
/// <list type="bullet">
/// <item><c>Wine=18.00</c> -- a line nobody has claimed yet.</item>
/// <item><c>Wine=18.00x2</c> -- two of them, 18.00 the pair.</item>
/// <item><c>Wine=18.00@&lt;id&gt;</c> -- one person had it.</item>
/// <item><c>Wine=18.00@&lt;id&gt;,&lt;id&gt;</c> -- shared evenly between two.</item>
/// <item><c>Wine=18.00@&lt;id&gt;*2,&lt;id&gt;</c> -- shared, one of them had twice as much.</item>
/// <item><c>Jacket=34.99/tax8.05</c> -- 8.05 of the bill's tax was charged on this line.</item>
/// <item><c>Bananas=1.99/notax</c> -- none of it was, which is also the default.</item>
/// <item><c>&lt;id&gt;#Wine=19.00</c> -- a line already on the bill, corrected.</item>
/// </list>
/// <para>
/// The id is what separates "the wine cost 19.00 after all" from "there was no wine, and
/// here is a different line". A bill is saved whole, so a line the request does not name is
/// one that has gone -- and without an id every line of a re-sent bill was a new line, which
/// meant correcting a price threw away who had what on every other line of the receipt.
/// <c>receipts show</c> prints the ids.
/// </para>
/// <para>
/// Claimants are the only thing that says how a line divides. A line could once be marked as
/// the table's without naming anybody; that is gone, because dividing something between
/// everybody is not what an itemised bill is for -- a category that wants an even split names
/// an even rule instead.
/// </para>
/// <para>
/// The tax is written per line, as the amount charged on it, which is what the till prints.
/// It used to be a flag, and the bill's single tax total was then weighed over the flagged
/// lines by price -- exact only where every taxed line carries one rate, and wrong by euros on
/// a supermarket receipt mixing 6% food with 23% household goods. The amounts have to come to
/// the receipt's <c>--tax</c>, which the server checks.
/// </para>
/// <para>
/// A line carries no tax unless it says so, which is what a restaurant bill under VAT wants:
/// the price includes the tax, the bill charges none on top, and no line needs marking.
/// </para>
/// <para>
/// Refused here rather than sent, for the reason <see cref="Pairs"/> gives: these are the
/// values a mistyped one ruins quietly. A price that did not parse is not an error the
/// server can report -- the subtotal simply comes out short, and the refusal blames the
/// arithmetic rather than the typo -- so a line that does not read is exit code 3 before any
/// request is made, and it says which line.
/// </para>
/// </remarks>
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
        // dropped and re-created, and fixing one mistyped price silently un-claimed the whole
        // receipt. The expense then could not be divided, and nothing said why.
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
        var claims = Array.Empty<ReceiptClaimInput>();
        var tax = 0m;

        // Lifted out before anything else is read, and spliced rather than truncated, so it
        // can be written on either side of the claimants -- "1.99/notax@<id>" and
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

        // Claimants last, so a name may contain anything but '=' and the price may contain
        // anything but '@'.
        if (rest.IndexOf('@') is var at and >= 0)
        {
            var who = rest[(at + 1)..].Trim();
            rest = rest[..at].Trim();

            claims = [.. Claims(option, value, who)];
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
            Claims = claims
        };
    }

    private static IEnumerable<ReceiptClaimInput> Claims(string option, string value, string text)
    {
        var seen = new HashSet<Guid>();

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var weight = 1;
            var id = part;

            if (part.IndexOf('*') is var star and >= 0)
            {
                id = part[..star].Trim();

                if (!int.TryParse(part[(star + 1)..].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out weight) || weight < 1)
                {
                    throw Invalid(option, value,
                        $"the weight after '*' in '{part}' should be a whole number of 1 or more");
                }
            }

            if (!Guid.TryParse(id, out var userId))
                throw Invalid(option, value, $"'{id}' is not a user id");

            // The server refuses this too, but it would refuse the whole bill after a round
            // trip and name only the line. Caught here it names the person.
            if (!seen.Add(userId))
            {
                throw CliException.Input(
                    $"{option} '{value}' claims {userId} twice on one line.",
                    "Somebody who had more of a line has a larger weight -- "
                    + $"{userId}*2 -- not a second claim.");
            }

            yield return new ReceiptClaimInput { UserId = userId, Weight = weight };
        }
    }

    private static decimal Number(string option, string value, string text, string what)
        => decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Invalid(option, value, $"'{text.Trim()}' is not {what}");

    private static CliException Invalid(string option, string value, string why)
        => CliException.Input(
            $"Could not read {option} '{value}': {why}.",
            $"Use {option} <name>=<price>[x<qty>][/notax][@<user-id>[*<weight>],...], "
            + $"e.g. {option} {Sample} or "
            + $"{option} Wine=18.00@3f25c1a8-1111-2222-3333-444455556666");
}

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
/// <item><c>Wine=18.00@even</c> -- shared between everybody, naming nobody.</item>
/// </list>
/// <para>
/// <c>even</c> is a word rather than an id and cannot collide with one: a guid never parses
/// as it. It is how a line says it is the table's without naming anybody, which is what
/// "these two are mine and the rest is shared" needs and what a list of claimants cannot
/// express -- naming everybody says the same thing today and stops saying it the moment
/// somebody joins. To put a line on one person, name them.
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

        var rest = value[(separator + 1)..].Trim();
        var claims = Array.Empty<ReceiptClaimInput>();
        var split = ReceiptItemSplit.Claimed;

        // Claimants last, so a name may contain anything but '=' and the price may contain
        // anything but '@'.
        if (rest.IndexOf('@') is var at and >= 0)
        {
            var who = rest[(at + 1)..].Trim();
            rest = rest[..at].Trim();

            switch (who.ToLowerInvariant())
            {
                case "even":
                    split = ReceiptItemSplit.Evenly;
                    break;

                default:
                    claims = [.. Claims(option, value, who)];
                    break;
            }
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
            Name = name,
            TotalPrice = totalPrice,
            Quantity = quantity,
            Split = split,
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
            $"Use {option} <name>=<price>[x<qty>][@<user-id>[*<weight>],...|@even], "
            + $"e.g. {option} {Sample}, {option} Wine=18.00@even, or "
            + $"{option} Wine=18.00@3f25c1a8-1111-2222-3333-444455556666");
}

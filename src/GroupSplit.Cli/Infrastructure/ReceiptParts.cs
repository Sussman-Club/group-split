using System.Globalization;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// One purchase on a split charge, written on one line of a shell:
/// <c>Groceries=1-4,7@&lt;group-id&gt;/&lt;category-id&gt;</c>.
/// </summary>
/// <remarks>
/// Read left to right, like <see cref="ReceiptItems"/> and for the same reason: a part
/// carries four things and splitting a warehouse receipt means typing several of them.
/// <list type="bullet">
/// <item><c>Clothes=5,6</c> -- two lines, kept on your own ledger.</item>
/// <item><c>Groceries=1-4@&lt;group-id&gt;</c> -- a run of lines, shared with a group.</item>
/// <item><c>Groceries=1-4@&lt;group-id&gt;/&lt;category-id&gt;</c> -- and filed under a category.</item>
/// <item><c>Jacket=&lt;item-id&gt;</c> -- a line named by its own id rather than its place.</item>
/// </list>
/// <para>
/// The category nests inside the group because that is where it lives: a category belongs to
/// one group, and naming one without a group would be an expense filed under a category no
/// ledger it lands in has. No <c>@</c> at all is the personal part -- the jacket that is
/// nobody's business but yours, which is the whole reason a charge gets split.
/// </para>
/// <para>
/// Lines are given by position or by id, and positions are what <c>receipts show</c> prints
/// in its first column. Ids are unambiguous and forty characters each; a warehouse bill is
/// twenty lines and nobody is pasting twenty guids, so ranges exist and are the ordinary way
/// to write this.
/// </para>
/// <para>
/// A position that is not on the bill is refused here rather than sent. The API would refuse
/// it too, naming an id the caller never typed -- and by then the interesting part, which
/// line of the paper they meant, is gone.
/// </para>
/// <para>
/// A charge nobody itemised is split the same way with an amount where the lines would be --
/// <c>Groceries=48.20@&lt;group-id&gt;</c> -- and <see cref="ParseAmounts"/> reads those.
/// Which of the two applies is never the caller's to choose and never guessed from the text:
/// the charge either has a bill or it does not, and the command has read it either way
/// before it parses anything.
/// </para>
/// </remarks>
public static class ReceiptParts
{
    /// <param name="lines">
    /// The bill's lines in the order <c>receipts show</c> prints them, which is what a
    /// position refers to.
    /// </param>
    public static IReadOnlyList<BankTransactionPartInput> Parse(
        string option, IEnumerable<string> values, IReadOnlyList<ReceiptItemResponse> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return [.. values.Select(value => One(option, value, lines))];
    }

    /// <summary>
    /// The same parts on a charge with no bill, where each says what it is worth instead of
    /// which lines it holds.
    /// </summary>
    /// <remarks>
    /// The amounts have to come to the charge exactly and the API says so when they do not,
    /// naming both figures. Not checked here: the charge is the bank's and this reads
    /// strings, so a second copy of that comparison would only ever be the one that was
    /// wrong.
    /// </remarks>
    public static IReadOnlyList<BankTransactionPartInput> ParseAmounts(
        string option, IEnumerable<string> values)
    {
        return [.. values.Select(value => OneAmount(option, value))];
    }

    private static BankTransactionPartInput OneAmount(string option, string value)
    {
        var (part, rest) = Head(option, value);

        if (!decimal.TryParse(rest, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            throw InvalidAmount(option, value, $"'{rest}' is not an amount");

        return part with { Amount = amount };
    }

    private static BankTransactionPartInput One(
        string option, string value, IReadOnlyList<ReceiptItemResponse> lines)
    {
        var (part, rest) = Head(option, value);

        return part with { ItemIds = [.. Lines(option, value, rest, lines)] };
    }

    /// <summary>
    /// Everything a part carries except what it holds: its name, and where it goes.
    /// </summary>
    /// <returns>The part so far, and the text between '=' and '@' for the caller to read.</returns>
    private static (BankTransactionPartInput Part, string Text) Head(string option, string value)
    {
        var separator = value.IndexOf('=');

        if (separator <= 0)
            throw Invalid(option, value, "it needs the form <name>=<lines>");

        var name = value[..separator].Trim();

        if (name.Length == 0)
            throw Invalid(option, value, "the part before '=' is empty");

        var rest = value[(separator + 1)..].Trim();

        Guid? groupId = null;
        Guid? categoryId = null;

        // Where it goes, last, so the list of lines may contain anything but '@'.
        if (rest.IndexOf('@') is var at and >= 0)
        {
            var where = rest[(at + 1)..].Trim();
            rest = rest[..at].Trim();

            var under = where.IndexOf('/');

            if (under >= 0)
            {
                categoryId = Id(option, value, where[(under + 1)..], "a category id");
                where = where[..under].Trim();
            }

            groupId = Id(option, value, where, "a group id");
        }

        if (rest.Length == 0)
            throw Invalid(option, value, "the part after '=' is empty");

        return (new BankTransactionPartInput
        {
            Name = name,
            GroupId = groupId,
            CategoryId = categoryId
        }, rest);
    }

    /// <summary>
    /// The line ids a part names, given as positions, ranges of positions, or ids.
    /// </summary>
    /// <remarks>
    /// Order is the caller's, and duplicates are left alone. Both are the API's to refuse: a
    /// line named twice is money counted twice, and it says so naming the line -- which is
    /// more use than this saying it about a token.
    /// </remarks>
    private static IEnumerable<Guid> Lines(
        string option, string value, string text, IReadOnlyList<ReceiptItemResponse> lines)
    {
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(token, out var itemId))
            {
                if (lines.All(line => line.Id != itemId))
                    throw Invalid(option, value, $"'{token}' is not a line on this bill");

                yield return itemId;
                continue;
            }

            // A range, which is how most of a warehouse bill gets written: the groceries are
            // the first fourteen lines and the clothes are the last two.
            if (token.IndexOf('-') is var dash and > 0)
            {
                var from = Position(option, value, token[..dash], lines);
                var to = Position(option, value, token[(dash + 1)..], lines);

                for (var at = Math.Min(from, to); at <= Math.Max(from, to); at++)
                    yield return lines[at - 1].Id;

                continue;
            }

            yield return lines[Position(option, value, token, lines) - 1].Id;
        }
    }

    private static int Position(
        string option, string value, string text, IReadOnlyList<ReceiptItemResponse> lines)
    {
        var written = text.Trim();

        if (!int.TryParse(written, out var position))
            throw Invalid(option, value, $"'{written}' is not a line number or a line id");

        if (position < 1 || position > lines.Count)
        {
            throw Invalid(option, value,
                $"there is no line {position} -- the bill has {lines.Count}");
        }

        return position;
    }

    private static Guid Id(string option, string value, string text, string what)
        => Guid.TryParse(text.Trim(), out var parsed)
            ? parsed
            : throw Invalid(option, value, $"'{text.Trim()}' is not {what}");

    private static CliException InvalidAmount(string option, string value, string why)
        => CliException.Input(
            $"Could not read {option} '{value}': {why}.",
            "This charge has no itemised bill, so a part says what it is worth rather than "
            + $"which lines it holds. Use {option} <name>=<amount>[@<group-id>[/<category-id>]] "
            + $"-- e.g. {option} \"Groceries=48.20@<group-id>\" and {option} \"Jacket=34.99\" "
            + "for the part that is yours alone. The parts have to come to the charge exactly.");

    private static CliException Invalid(string option, string value, string why)
        => CliException.Input(
            $"Could not read {option} '{value}': {why}.",
            $"Use {option} <name>=<lines>[@<group-id>[/<category-id>]], where <lines> is "
            + $"line numbers, ranges or ids -- e.g. {option} \"Groceries=1-4@<group-id>\" "
            + $"and {option} \"Clothes=5,6\" for the part that is yours alone.");
}

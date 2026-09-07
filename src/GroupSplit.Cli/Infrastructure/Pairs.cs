using System.Globalization;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The <c>id=value</c> form used wherever a command has to name people and numbers on one
/// line: a split's shares, a rule's percentages, a rule's whole shares.
/// </summary>
/// <remarks>
/// Rejected here rather than sent, because these are the values a mistyped one ruins
/// quietly. A share the server never received is not an error it can report -- the totals
/// simply come out somebody short -- so a pair that does not parse is exit code 3 before
/// any request is made, and it says which pair.
/// </remarks>
public static class Pairs
{
    public static IReadOnlyList<SplitInput> Splits(string option, IEnumerable<string> values)
        => Decimals(option, values)
            .Select(pair => new SplitInput { UserId = pair.Key, Amount = pair.Value })
            .ToList();

    public static Dictionary<Guid, decimal> Decimals(string option, IEnumerable<string> values)
        => Parse<decimal>(option, values, "21.25", text =>
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null);

    public static Dictionary<Guid, int> Ints(string option, IEnumerable<string> values)
        => Parse<int>(option, values, "2", text =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null);

    /// <param name="sample">
    /// A value of the right shape, for the remediation line. It is what makes the error
    /// actionable without reading the help: "not a number" says what is wrong, and this
    /// says what to type instead.
    /// </param>
    private static Dictionary<Guid, T> Parse<T>(
        string option, IEnumerable<string> values, string sample, Func<string, T?> read)
        where T : struct
    {
        var parsed = new Dictionary<Guid, T>();

        foreach (var value in values)
        {
            var separator = value.IndexOf('=');

            if (separator <= 0)
            {
                throw Invalid(option, value, sample, "it needs the form <user-id>=<value>");
            }

            if (!Guid.TryParse(value[..separator].Trim(), out var userId))
            {
                throw Invalid(option, value, sample, "the part before '=' is not a user id");
            }

            if (read(value[(separator + 1)..].Trim()) is not { } amount)
            {
                throw Invalid(option, value, sample, $"the part after '=' should look like {sample}");
            }

            // Last-one-wins would silently drop one of two values for the same person, and
            // the sum is what the API checks -- so the duplicate is the error, not the total.
            if (!parsed.TryAdd(userId, amount))
            {
                throw CliException.Input(
                    $"{option} names {userId} more than once.",
                    $"Give each member one {option} value.");
            }
        }

        return parsed;
    }

    private static CliException Invalid(string option, string value, string sample, string why)
        => CliException.Input(
            $"Could not read {option} '{value}': {why}.",
            $"Use {option} <user-id>={sample}, "
            + $"e.g. {option} 3f25c1a8-1111-2222-3333-444455556666={sample}");
}

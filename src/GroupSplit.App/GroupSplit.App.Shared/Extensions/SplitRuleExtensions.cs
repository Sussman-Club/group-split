using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Extensions;

/// <summary>
/// One person's standing in a division: what the rule gives them, and what that comes to as
/// a proportion of the whole.
/// </summary>
/// <param name="Held">
/// What the rule actually says -- "2 shares", "60%" -- because that is the number somebody
/// typed and the one they will look for.
/// </param>
/// <param name="Share">
/// What it works out as, or null when the rule states no proportion at all. Both are worth
/// showing: three people on 2, 2 and 1 shares is not obviously 40/40/20.
/// </param>
public sealed record SplitRuleWeight(Guid UserId, string Name, string Held, decimal? Share);

/// <summary>
/// Saying what a division does, in a phrase.
/// </summary>
/// <remarks>
/// One place rather than the four that were each growing their own: a category row, a rule
/// row, a version in a rule's history and the option list that corrects which version an
/// expense records all print the same sentence about the same object, and the one that was
/// written first said "By shares, between 3" -- true, and no use to somebody asking who
/// was on what.
/// <para>
/// Names are optional throughout. A caller with the group's membership in hand gets people
/// named; one without gets a count, which is what the phrase degrades to rather than a
/// column of raw ids.
/// </para>
/// </remarks>
public static class SplitRuleExtensions
{
    extension(SplitRuleDto? definition)
    {
        /// <summary>What this division does, as a phrase to put on a row.</summary>
        public string Summary(IReadOnlyDictionary<Guid, string>? names = null) => definition switch
        {
            PayerSplitRuleDto => "All on whoever paid",

            EvenSplitRuleDto { Among.Count: 0 } => "Evenly, between everyone in the group",

            EvenSplitRuleDto even => names is null
                ? $"Evenly, between {even.Among.Count}"
                : $"Evenly, between {Listed(even.Among.Select(id => Named(names, id)))}",

            PercentSplitRuleDto percent => names is null
                ? $"By percentage, between {percent.Percentages.Count}"
                : "By percentage — " + Listed(
                    Ordered(percent.Percentages).Select(held =>
                        $"{Named(names, held.Key)} {Trimmed(held.Value)}%"),
                    ", "),

            SharesSplitRuleDto shares => names is null
                ? $"By shares, between {shares.Shares.Count}"
                : "By shares — " + Listed(
                    Ordered(shares.Shares).Select(held => $"{Named(names, held.Key)} {held.Value}"),
                    ", "),

            // A category that names no rule divides evenly, and so does a rule whose type
            // nobody has chosen yet. Neither is broken and both divide the same way.
            _ => "Evenly, between everyone in the group"
        };

        /// <summary>
        /// The weights alone, as a ratio -- "2 : 2 : 1". For a row too narrow for the
        /// phrase, where what a person is picking between is which shape the division had.
        /// </summary>
        public string Ratio() => definition switch
        {
            PercentSplitRuleDto percent when percent.Percentages.Count > 0 =>
                string.Join(" : ", Ordered(percent.Percentages).Select(held => $"{Trimmed(held.Value)}%")),

            SharesSplitRuleDto shares when shares.Shares.Count > 0 =>
                string.Join(" : ", Ordered(shares.Shares).Select(held => held.Value.ToString())),

            PayerSplitRuleDto => "whoever paid",

            _ => "evenly"
        };

        /// <summary>
        /// Who the division names and what each of them holds, in the order the rule states
        /// it. Empty for a division that names nobody -- the whole group evenly, or all on
        /// whoever paid -- which has no per-person row to draw.
        /// </summary>
        public IReadOnlyList<SplitRuleWeight> Weights(IReadOnlyDictionary<Guid, string>? names = null)
        {
            switch (definition)
            {
                case PercentSplitRuleDto percent when percent.Percentages.Count > 0:
                    return
                    [
                        .. Ordered(percent.Percentages).Select(held => new SplitRuleWeight(
                            held.Key, Named(names, held.Key), $"{Trimmed(held.Value)}%", held.Value))
                    ];

                case SharesSplitRuleDto shares when shares.Shares.Count > 0:
                {
                    var total = shares.Shares.Values.Sum();

                    return
                    [
                        .. Ordered(shares.Shares).Select(held => new SplitRuleWeight(
                            held.Key,
                            Named(names, held.Key),
                            $"{held.Value} {(held.Value == 1 ? "share" : "shares")}",
                            // A rule whose shares are all zero divides nothing and the API
                            // refuses to save one, but a half-typed form can hold it.
                            total == 0 ? null : Math.Round(held.Value * 100m / total, 1)))
                    ];
                }

                case EvenSplitRuleDto even when even.Among.Count > 0:
                {
                    var each = Math.Round(100m / even.Among.Count, 1);

                    return
                    [
                        .. even.Among.Select(id =>
                            new SplitRuleWeight(id, Named(names, id), "1 share", each))
                    ];
                }

                default:
                    return [];
            }
        }
    }

    /// <summary>
    /// Heaviest first, and by id within a tie, so the same rule reads the same way twice.
    /// Dictionary order is not order.
    /// </summary>
    private static IEnumerable<KeyValuePair<Guid, T>> Ordered<T>(Dictionary<Guid, T> held) where T : struct =>
        held.OrderByDescending(entry => Convert.ToDecimal(entry.Value)).ThenBy(entry => entry.Key);

    private static string Named(IReadOnlyDictionary<Guid, string>? names, Guid id) =>
        names is not null && names.TryGetValue(id, out var name) ? name : "someone who has left";

    /// <summary>33.30 is 33.3, and 50.00 is 50 -- trailing zeroes are not precision.</summary>
    private static string Trimmed(decimal value) => value.ToString("0.##");

    /// <summary>
    /// "Ana, Lu and Marta" for a list somebody reads as a sentence, or plain commas where
    /// each entry already carries a figure and the "and" would read as part of it.
    /// </summary>
    private static string Listed(IEnumerable<string> parts, string? separator = null)
    {
        var all = parts.ToArray();

        if (separator is not null || all.Length < 2)
            return string.Join(separator ?? ", ", all);

        return $"{string.Join(", ", all[..^1])} and {all[^1]}";
    }
}

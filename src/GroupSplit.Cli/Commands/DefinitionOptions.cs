using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The ways a split rule can divide, as command-line flags -- one more flag than there are
/// kinds, because the one that names a person offers to look your own id up.
/// </summary>
/// <remarks>
/// On the wire the division is one polymorphic object told apart by <c>$type</c>, which a
/// caller could be asked to write out as JSON. It is a set of flags instead: a flag is
/// typed, is completed by the shell, appears in <c>groupsplit schema</c> with its arity, and
/// puts a mistyped percentage in front of the parser rather than in front of the
/// serializer. Exactly one may be given, and which one it was is the rule's kind.
/// <para>
/// One instance per command, because a <see cref="Option{T}"/> carries no state but is
/// identified by reference when the parse result is read.
/// </para>
/// </remarks>
public sealed class DefinitionOptions
{
    private readonly Option<bool> _even = new("--even")
    {
        Description = "Divide equally. With no --among, between whoever is in the group at the time."
    };

    private readonly Option<Guid[]> _among = new("--among")
    {
        Description = "Restrict --even to these members. Repeatable.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<Guid?> _sole = new("--sole")
    {
        Description = "Do not share it: all of it is for this member, whoever paid."
    };

    private readonly Option<bool> _payer = new("--payer")
    {
        Description = "Do not share it: all of it is for you. The same as --sole with your own "
                      + "id, which this looks up so you do not have to."
    };

    private readonly Option<string[]> _percent = new("--percent")
    {
        Description = "Divide by percentages, as <user-id>=<percent>. Repeatable, e.g. 33.33.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<string[]> _shares = new("--shares")
    {
        Description = "Divide by whole shares, as <user-id>=<shares>. Repeatable.",
        AllowMultipleArgumentsPerToken = true
    };

    // No --itemized. "Divide it by the bill" is the one division nobody writes: every group
    // is given exactly one and the API refuses a second, because the rule holds no settings
    // for a second to differ by -- what it divides by lives on the receipt, one line at a
    // time. The group's own is in `groupsplit rules list`, and an expense is pointed at it
    // like any other rule. A flag here would be one the server answers 409 to every time.

    public void AddTo(Command command)
    {
        command.Options.Add(_even);
        command.Options.Add(_among);
        command.Options.Add(_sole);
        command.Options.Add(_payer);
        command.Options.Add(_percent);
        command.Options.Add(_shares);
    }

    /// <summary>
    /// The division these flags describe, or <paramref name="current"/> when none was given.
    /// </summary>
    /// <remarks>
    /// Two given is refused rather than resolved by precedence: a caller passing both
    /// <c>--even</c> and <c>--shares</c> has one of the two in mind, and picking for them
    /// would write the other.
    /// <para>
    /// <c>--payer</c> is the one flag that cannot be read from the command line alone: the
    /// rule it describes names a person, and the person is whoever is signed in, so it takes
    /// a round trip to find out. That is the whole of the difference -- what reaches the API
    /// is a rule naming a member, the same as <c>--sole</c> would have written.
    /// </para>
    /// </remarks>
    public async Task<SplitRuleDto?> Read(
        CliContext context, ParseResult parse, SplitRuleDto? current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var chosen = new List<string>();

        if (parse.GetValue(_even) || parse.GetResult(_among) is not null) chosen.Add("--even");
        if (parse.GetResult(_sole) is not null) chosen.Add("--sole");
        if (parse.GetValue(_payer)) chosen.Add("--payer");
        if (parse.GetResult(_percent) is not null) chosen.Add("--percent");
        if (parse.GetResult(_shares) is not null) chosen.Add("--shares");

        if (chosen.Count > 1)
        {
            throw CliException.Input(
                $"{string.Join(" and ", chosen)} describe different divisions.",
                "Pass exactly one of --even, --sole, --payer, --percent or --shares.");
        }

        if (chosen.Count == 0)
        {
            return current;
        }

        return chosen[0] switch
        {
            "--even" => new EvenSplitRuleDto(parse.GetValue(_among) ?? []),
            "--sole" => new SoleSplitRuleDto(parse.GetValue(_sole) ?? Guid.Empty),
            "--payer" => new SoleSplitRuleDto((await context.Users.GetCurrentUserAsync(ct)).Id),
            "--percent" => new PercentSplitRuleDto
            {
                Percentages = Pairs.Decimals("--percent", parse.GetValue(_percent) ?? [])
            },
            _ => new SharesSplitRuleDto
            {
                Shares = Pairs.Ints("--shares", parse.GetValue(_shares) ?? [])
            }
        };
    }
}

/// <summary>Reading a division back out, for the commands that print one.</summary>
public static class Definitions
{
    public static string Describe(SplitRuleDto definition) => definition switch
    {
        EvenSplitRuleDto { Among.Count: 0 } => "evenly, between the whole group",
        EvenSplitRuleDto even => $"evenly, between {even.Among.Count} named members",
        SoleSplitRuleDto => "not shared: all of it is for one member",
        ItemizedSplitRuleDto => "by the bill: what each person had, plus its tax and a share of the tip",
        PercentSplitRuleDto => "by percentage",
        SharesSplitRuleDto => "by whole shares",
        _ => definition.GetType().Name
    };

    /// <summary>
    /// The per-member figures, as text, or empty for a division that names nobody. Text
    /// because a percentage and a share count are not the same number and the column heading
    /// is the same either way.
    /// </summary>
    public static IReadOnlyList<(Guid UserId, string Share)> Shares(SplitRuleDto definition)
        => definition switch
        {
            EvenSplitRuleDto even => [.. even.Among.Select(userId => (userId, "an equal part"))],
            PercentSplitRuleDto percent =>
                [.. percent.Percentages.Select(pair => (pair.Key, $"{pair.Value:0.##}%"))],
            SharesSplitRuleDto shares =>
                [.. shares.Shares.Select(pair => (pair.Key, pair.Value == 1 ? "1 share" : $"{pair.Value} shares"))],
            SoleSplitRuleDto sole => [(sole.UserId, "all of it")],
            _ => []
        };
}

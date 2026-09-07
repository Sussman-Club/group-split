using System.CommandLine;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Answers the <c>[suggest]</c> directive the completion scripts call on every Tab.
/// <para>
/// System.CommandLine ships a handler for this directive already, and the reason this
/// replaces it is what that handler considers an answer: everything the parser would
/// accept at the cursor. At <c>groupsplit &lt;TAB&gt;</c> that is the ten commands plus
/// every recursive option plus <c>-?</c>, <c>/?</c> and <c>/h</c> -- the DOS-style help
/// aliases it adds for Windows. A pager listing all of it at a position where only a
/// command is valid reads as noise, and none of it carries a description, so the shell
/// shows a bare column of words.
/// </para>
/// <para>
/// So: options appear once the word being completed starts with a dash and not before,
/// the DOS aliases never appear, and every line carries the description the help already
/// has. The tree stays the single source of truth -- this only decides what is worth
/// showing, never what exists.
/// </para>
/// <para>
/// The one place that rule left silent is a positional nobody can complete.
/// <c>transactions create &lt;TAB&gt;</c> wants a name: free text, no candidate list, so
/// the answer was nothing at all -- indistinguishable, at the prompt, from a completion
/// that was never installed. There this emits a hint instead: the name and description
/// the help already gives the argument, on a line with an empty label. See
/// <see cref="DescribeNextArgument"/> for why options come with it.
/// </para>
/// </summary>
public static class Suggestions
{
    /// <summary>
    /// A label and its description, tab-separated. Chosen because fish reads exactly this
    /// shape natively, and because a tab cannot occur inside a description.
    /// </summary>
    private const char Separator = '\t';

    /// <summary>
    /// Writes the suggestions for <paramref name="args"/> if it is a suggest request, and
    /// reports whether it was. Called before parsing: the directive's payload is a command
    /// line of its own, and the parse that matters is of that, not of the two arguments the
    /// shell handed to this process.
    /// </summary>
    public static bool TryWrite(string[] args, RootCommand root, TextWriter stdout)
    {
        if (args.Length == 0 || !TryReadPosition(args[0], out var position))
        {
            return false;
        }

        // A shell with nothing typed after the command name still sends the line, so an
        // absent payload means an empty line rather than a malformed request.
        var line = args.Length > 1 ? args[1] : string.Empty;

        // The shells report the cursor, which can sit past what they chose to send.
        position = Math.Clamp(position ?? line.Length, 0, line.Length);

        var parseResult = root.Parse(line);
        var word = parseResult.GetCompletionContext().WordToComplete;

        var candidates = parseResult.GetCompletions(position)
            .Where(item => IsWorthShowing(item.Label, word))
            .Select(item => (item.Label, Description: Flatten(item.Detail)))
            .DistinctBy(item => item.Label)
            .ToList();

        var values = candidates.Where(item => !item.Label.StartsWith('-')).ToList();

        // Only when there is nothing else to say. An argument with a closed set of values
        // -- `completion <TAB>`, `-o <TAB>` -- completes for real, and a hint beside the
        // values it is describing would be repeating them.
        var hint = word.Length == 0 && values.Count == 0
            ? DescribeNextArgument(parseResult)
            : null;

        if (hint is not null)
        {
            stdout.WriteLine(Separator + hint);
        }

        var suggestions = (word.StartsWith('-') || hint is not null ? candidates : values)
            // Commands and values first, then options: the thing being named is what the
            // reader is usually after, and it is the half a pager truncates last.
            .OrderBy(item => item.Label.StartsWith('-'))
            .ThenBy(item => item.Label, StringComparer.Ordinal);

        foreach (var (label, description) in suggestions)
        {
            stdout.WriteLine(
                description.Length == 0 ? label : label + Separator + description);
        }

        return true;
    }

    /// <summary>
    /// Reads <c>[suggest]</c> or <c>[suggest:42]</c>. A directive without a position is
    /// valid and means "the end of the line", which is what a shell sends when it has
    /// already cut the line at the cursor.
    /// </summary>
    private static bool TryReadPosition(string argument, out int? position)
    {
        position = null;

        if (argument is not ['[', .., ']'])
        {
            return false;
        }

        var body = argument[1..^1];
        var colon = body.IndexOf(':');
        var name = colon < 0 ? body : body[..colon];

        if (name != "suggest")
        {
            return false;
        }

        if (colon >= 0 && int.TryParse(body[(colon + 1)..], out var parsed))
        {
            position = parsed;
        }

        return true;
    }

    /// <summary>
    /// The next argument the deepest matched command still wants, as <c>&lt;name&gt;</c>
    /// and its description, or null when it wants none.
    /// <para>
    /// A hint is written with an empty label, which is fish's way of showing a note rather
    /// than something to pick: the pager renders the description alone and accepting the
    /// entry inserts nothing. It comes at a price -- fish completes a sole candidate
    /// without asking, and a sole empty one lands on the command line as a literal
    /// <c>''</c>. So a hint must never be the only line, which is why the caller stops
    /// gating options at a position that produced one.
    /// </para>
    /// <para>
    /// Letting them through here does not undo the rule that hides them elsewhere. At a
    /// command position options are noise because they crowd out the commands, which are
    /// the answer; at a positional there is no answer to crowd out, and an option is the
    /// only other thing the parser would accept.
    /// </para>
    /// </summary>
    private static string? DescribeNextArgument(ParseResult parseResult)
    {
        var argument = parseResult.CommandResult.Command.Arguments
            .FirstOrDefault(argument => parseResult.GetResult(argument)?.Tokens.Count is null or 0);

        if (argument is null)
        {
            return null;
        }

        // Two spaces, not a tab: the whole line is one field to every shell that reads it,
        // and this reads the way the "Arguments:" section of the help already does.
        var description = Flatten(argument.Description);

        return description.Length == 0
            ? $"<{argument.Name}>"
            : $"<{argument.Name}>  {description}";
    }

    private static bool IsWorthShowing(string label, string word)
    {
        // `/?` and `/h` are conveniences for cmd.exe that System.CommandLine adds
        // everywhere. No shell this CLI writes a script for would ever want them.
        if (label.StartsWith('/') || label == "-?")
        {
            return false;
        }

        // The parser matches loosely enough that `groups l` offers `--help`. Every shell
        // filters again on its own, but the directive is also read by people and scripts.
        return word.Length == 0 || label.StartsWith(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One line per suggestion is the whole protocol, so a description that wraps would
    /// desynchronise every consumer of it.
    /// </summary>
    private static string Flatten(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

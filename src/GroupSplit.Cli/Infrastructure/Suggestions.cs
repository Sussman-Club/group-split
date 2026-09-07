using System.CommandLine;
using System.CommandLine.Completions;

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
        var cursor = Math.Clamp(position ?? line.Length, 0, line.Length);

        (line, cursor) = WithoutProgramName(line, cursor);

        var parseResult = root.Parse(line);
        var word = parseResult.GetCompletionContext().WordToComplete;

        if (HasTokenTheParserCouldNotPlace(parseResult, word))
        {
            return true;
        }

        var candidates = parseResult.GetCompletions(cursor)
            .Where(item => IsWorthShowing(item.Label, word))
            .Select(item => (item.Label, Description: Flatten(item.Detail)))
            .DistinctBy(item => item.Label)
            .ToList();

        var supplied = ArgumentsAlreadyGivenAValue(parseResult, word);
        var stale = ValuesOf(supplied, parseResult.GetCompletionContext());

        var values = candidates
            .Where(item => !item.Label.StartsWith('-') && !stale.Contains(item.Label))
            .ToList();

        // Only when there is nothing else to say. An argument with a closed set of values
        // -- `completion <TAB>`, `-o <TAB>` -- completes for real, and a hint beside the
        // values it is describing would be repeating them.
        var hint = word.Length == 0 && values.Count == 0
            ? DescribeNextArgument(parseResult, supplied)
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
    /// Drops the program name from the front of the line, and moves the cursor with it.
    /// <para>
    /// The payload of the directive is a whole command line, and its first word is this
    /// program under whatever name it was reached by -- <c>groupsplit</c>, but equally
    /// <c>./groupsplit</c>, an absolute path, or a symlink someone named <c>gs</c>. Left
    /// in place it is a token the parser can only match when that name happens to equal
    /// the root command's, so removing it is what makes everything downstream -- the
    /// completions, and above all <see cref="HasTokenTheParserCouldNotPlace"/> -- depend
    /// on what was typed rather than on what the binary is called.
    /// </para>
    /// </summary>
    private static (string Line, int Cursor) WithoutProgramName(string line, int cursor)
    {
        var end = 0;

        while (end < line.Length && char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        // A path with a space in it arrives quoted, and the closing quote ends the word.
        if (end < line.Length && line[end] is '"' or '\'')
        {
            var quote = line[end++];

            while (end < line.Length && line[end] != quote)
            {
                end++;
            }

            if (end < line.Length)
            {
                end++;
            }
        }
        else
        {
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
            {
                end++;
            }
        }

        return (line[end..], Math.Clamp(cursor - end, 0, line.Length - end));
    }

    /// <summary>
    /// Whether the line holds a word the parser could not place -- which makes every
    /// suggestion misleading, so the answer becomes nothing at all.
    /// <para>
    /// <c>groupsplit blablabla &lt;TAB&gt;</c> otherwise offers the ten root commands, the
    /// same answer as <c>groupsplit &lt;TAB&gt;</c>: System.CommandLine leaves an
    /// unrecognised token where it lies rather than descending, so the cursor still counts
    /// as being at the root. Offering <c>groups</c> there suggests <c>blablabla groups</c>
    /// is a command line, and it is not.
    /// </para>
    /// <para>
    /// The word being completed is itself unplaced until it is finished, so it is excluded
    /// -- without that, <c>groupsplit gr&lt;TAB&gt;</c> would stop offering <c>groups</c>.
    /// </para>
    /// </summary>
    private static bool HasTokenTheParserCouldNotPlace(ParseResult parseResult, string word)
    {
        var unplaced = parseResult.UnmatchedTokens;

        var stillBeingTyped = word.Length > 0 && unplaced.Count > 0 && unplaced[^1] == word;

        return unplaced.Count - (stillBeingTyped ? 1 : 0) > 0;
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
    /// The arguments of the deepest matched command that already hold a value.
    /// <para>
    /// The word under the cursor is not one of them, however far into it the typing got:
    /// the parser binds a half-written <c>b</c> to <c>shell</c> the moment it is typed, and
    /// counting that as supplied is what would stop <c>completion b&lt;TAB&gt;</c> from
    /// ever reaching <c>Bash</c>. Only the last one filled can be the word, so only that
    /// one is reconsidered.
    /// </para>
    /// </summary>
    private static List<Argument> ArgumentsAlreadyGivenAValue(ParseResult parseResult, string word)
    {
        var supplied = parseResult.CommandResult.Command.Arguments
            .Where(argument => parseResult.GetResult(argument)?.Tokens.Count > 0)
            .ToList();

        if (word.Length > 0
            && supplied.Count > 0
            && parseResult.GetResult(supplied[^1])!.Tokens[^1].Value == word)
        {
            supplied.RemoveAt(supplied.Count - 1);
        }

        return supplied;
    }

    /// <summary>
    /// What those arguments would still offer, and the parser would no longer accept.
    /// <para>
    /// An argument with a closed set keeps offering that set after it is filled:
    /// <c>completion bash &lt;TAB&gt;</c> proposes all four shells again, and typing one
    /// makes the line unparseable. The same library gets this right for an option's value
    /// -- <c>-o Json &lt;TAB&gt;</c> moves on to the commands -- so the asymmetry is the
    /// argument's alone, and this closes it. Only values are withdrawn: a subcommand or an
    /// option is still valid at a position where every argument is satisfied.
    /// </para>
    /// </summary>
    private static HashSet<string> ValuesOf(List<Argument> supplied, CompletionContext context) =>
        supplied
            .SelectMany(argument => argument.GetCompletions(context))
            .Select(item => item.Label)
            .ToHashSet(StringComparer.Ordinal);

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
    private static string? DescribeNextArgument(ParseResult parseResult, List<Argument> supplied)
    {
        var argument = parseResult.CommandResult.Command.Arguments
            .FirstOrDefault(argument => !supplied.Contains(argument));

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

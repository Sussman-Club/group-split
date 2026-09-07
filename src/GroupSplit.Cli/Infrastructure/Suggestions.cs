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
        var wantsOptions = word.StartsWith('-');

        var suggestions = parseResult.GetCompletions(position)
            .Where(item => IsWorthShowing(item.Label, word, wantsOptions))
            .Select(item => (item.Label, Description: Flatten(item.Detail)))
            // Commands and values first, then options: the thing being named is what the
            // reader is usually after, and it is the half a pager truncates last.
            .OrderBy(item => item.Label.StartsWith('-'))
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .DistinctBy(item => item.Label);

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

    private static bool IsWorthShowing(string label, string word, bool wantsOptions)
    {
        // `/?` and `/h` are conveniences for cmd.exe that System.CommandLine adds
        // everywhere. No shell this CLI writes a script for would ever want them.
        if (label.StartsWith('/') || label == "-?")
        {
            return false;
        }

        if (!wantsOptions && label.StartsWith('-'))
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

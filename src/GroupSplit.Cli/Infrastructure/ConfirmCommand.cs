using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.RegularExpressions;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Writes the <c>confirmCommand</c> of a confirmation envelope back out of the invocation
/// that asked for it.
/// </summary>
/// <remarks>
/// Built from the parse rather than written by hand at each gate, because a hand-written one
/// says only what its author remembered. Those dropped <c>--profile</c>, so confirming a
/// prompt about the staging account deleted the account on the default profile; they dropped
/// <c>--date</c>, so a backdated repayment landed as today. Every option the caller gave is
/// here, in the order it belongs to the command it was given to, and the only thing added is
/// <c>--yes</c>.
/// <para>
/// Three things are rewritten on the way, each so the string means the same thing when it is
/// run somewhere else: a file is named by its absolute path, since the caller may re-run it
/// from another directory; every word is quoted for a POSIX shell when it needs to be; and a
/// value beginning with <c>-</c> or <c>@</c> is joined to its option with <c>=</c>, so it can
/// be read as neither another option nor a response file.
/// </para>
/// </remarks>
public static partial class ConfirmCommand
{
    /// <summary>The name a user types. Not the root command's, which is the process's.</summary>
    private const string Executable = "groupsplit";

    /// <summary>
    /// The words a POSIX shell and PowerShell both pass through untouched. Anything else
    /// is quoted. Narrower than POSIX needs on purpose: <c>,</c> is an operator in
    /// PowerShell and <c>@</c> starts a splat there.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-+=:./%]+$")]
    private static partial Regex Bare();

    /// <param name="parseResult">The invocation that reached the gate.</param>
    /// <param name="pinned">
    /// Options to state with a value the command worked out rather than one it was given, so
    /// confirming does what the prompt described even if what it was worked out from has
    /// moved since. <c>settle pay</c> without <c>--amount</c> pays what is outstanding, and
    /// the prompt names that figure; confirming an hour later pays the figure named.
    /// </param>
    public static string From(ParseResult parseResult, IReadOnlyDictionary<Option, string>? pinned = null)
    {
        var words = new List<string> { Executable };

        var chain = new List<CommandResult>();
        for (var result = parseResult.CommandResult; result is not null; result = result.Parent as CommandResult)
        {
            chain.Insert(0, result);
        }

        var leaf = chain[^1];
        var trailing = new List<string>();

        foreach (var command in chain)
        {
            if (command != chain[0])
            {
                words.Add(command.Command.Name);
            }

            var arguments = command.Children.OfType<ArgumentResult>()
                .SelectMany(argument => argument.Tokens.Select(token => Value(argument.Argument, token.Value)))
                .ToList();

            var options = command.Children.OfType<OptionResult>()
                .Where(option => !option.Implicit && option.Option != GlobalOptions.Yes)
                .SelectMany(option => Option(option.Option, pinned is not null && pinned.TryGetValue(option.Option, out var value)
                    ? [value]
                    : option.Tokens.Select(token => Value(option.Option, token.Value)).ToList()))
                .ToList();

            // A positional value that looks like an option only parsed as a value because it
            // came after `--`. It goes back after one, at the very end, where nothing that
            // follows can be an option.
            if (command == leaf && arguments.Any(argument => argument.StartsWith('-')))
            {
                words.AddRange(options);
                trailing.Add("--");
                trailing.AddRange(arguments);
                continue;
            }

            words.AddRange(arguments);
            words.AddRange(options);
        }

        if (pinned is not null)
        {
            foreach (var (option, value) in pinned)
            {
                if (parseResult.GetResult(option) is not { Implicit: false })
                {
                    words.AddRange(Option(option, [value]));
                }
            }
        }

        words.Add(GlobalOptions.Yes.Name);
        words.AddRange(trailing);

        return string.Join(' ', words.Select(Quote));
    }

    /// <summary>
    /// The option's own name rather than the alias it was given by, once per value: that is
    /// valid for an option taking one value, for one taking many, and for a flag with none.
    /// </summary>
    private static IEnumerable<string> Option(Option option, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            yield return option.Name;
            yield break;
        }

        foreach (var value in values)
        {
            if (value.StartsWith('-') || value.StartsWith('@'))
            {
                yield return $"{option.Name}={value}";
            }
            else
            {
                yield return option.Name;
                yield return value;
            }
        }
    }

    /// <summary>A file named relative to where the prompt ran, named from anywhere.</summary>
    private static string Value(Symbol symbol, string value)
    {
        var type = symbol switch
        {
            Argument argument => argument.ValueType,
            Option option => option.ValueType,
            _ => typeof(string)
        };

        var element = type.IsArray ? type.GetElementType()! : type;

        return typeof(FileSystemInfo).IsAssignableFrom(element) ? Path.GetFullPath(value) : value;
    }

    /// <summary>
    /// Single quotes, which turn off every expansion in a POSIX shell and in PowerShell
    /// alike. A quote inside one closes it, escapes itself and reopens it.
    /// </summary>
    public static string Quote(string word)
    {
        if (Bare().IsMatch(word))
        {
            return word;
        }

        var quoted = new StringBuilder("'");
        quoted.Append(word.Replace("'", @"'\''"));
        quoted.Append('\'');

        return quoted.ToString();
    }
}

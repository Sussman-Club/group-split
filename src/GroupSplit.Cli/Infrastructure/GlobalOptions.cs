using System.CommandLine;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Output;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The options every command accepts. Declared once and marked recursive so they read the
/// same at any depth -- `groupsplit --json groups list` and `groupsplit groups list --json`
/// both work, which matters because a caller composing a command line should not have to
/// know where in the tree a flag was declared.
/// </summary>
public static class GlobalOptions
{
    public static readonly Option<string?> Server = new("--server")
    {
        Description = $"Server origin, e.g. https://groupsplit.example.com. "
                      + $"Overrides {EnvironmentVariables.Server} and the config file.",
        Recursive = true
    };

    public static readonly Option<string?> Profile = new("--profile")
    {
        Description = "Config profile to read the server from.",
        Recursive = true
    };

    public static readonly Option<OutputFormat> Output = new Option<OutputFormat>("--output", "-o")
    {
        Description = "Output format: auto, text or json. auto is text on a terminal, json when piped.",
        DefaultValueFactory = _ => OutputFormat.Auto,
        Recursive = true
    }.WithDescribedValues();

    public static readonly Option<bool> Json = new("--json")
    {
        Description = "Shorthand for --output json.",
        Recursive = true
    };

    public static readonly Option<string[]> Fields = new("--fields")
    {
        Description = "Comma-separated top-level fields to keep in JSON output.",
        AllowMultipleArgumentsPerToken = true,
        Recursive = true
    };

    public static readonly Option<bool> NoColor = new("--no-color")
    {
        Description = "Disable colour. Also honours the NO_COLOR environment variable.",
        Recursive = true
    };

    public static readonly Option<bool> Quiet = new("--quiet", "-q")
    {
        Description = "Suppress progress and advisory messages on stderr. Errors still print.",
        Recursive = true
    };

    public static readonly Option<bool> Yes = new("--yes", "-y")
    {
        Description = "Confirm mutations without prompting.",
        Recursive = true
    };

    public static void AddTo(Command command)
    {
        command.Options.Add(Server);
        command.Options.Add(Profile);
        command.Options.Add(Output);
        command.Options.Add(Json);
        command.Options.Add(Fields);
        command.Options.Add(NoColor);
        command.Options.Add(Quiet);
        command.Options.Add(Yes);
    }

    /// <summary>
    /// --json wins over --output when both appear, because it is the more explicit of the
    /// two and someone passing it plainly wants JSON.
    /// </summary>
    public static OutputSettings ReadOutputSettings(ParseResult parseResult)
    {
        var format = parseResult.GetValue(Json) ? OutputFormat.Json
            : Parsed(Output) ? parseResult.GetValue(Output)
            : OutputFormat.Auto;

        // A value is only read once its own token parsed. This method also runs while a
        // usage error is being reported, and the option that failed may be this very one:
        // reading it then throws, and the CLI would crash on its way to telling the caller
        // what they mistyped. `groupsplit -o bogus` did exactly that.
        bool Parsed(Option option) => parseResult.GetResult(option)?.Errors.Any() != true;

        // Accept both --fields a,b and --fields a b, since callers assume one or the other.
        var fields = parseResult.GetValue(Fields)?
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        return OutputSettings.Resolve(
            format,
            parseResult.GetValue(NoColor),
            parseResult.GetValue(Quiet),
            fields);
    }
}

using GroupSplit.Cli.Output;

namespace GroupSplit.Cli.Output;

/// <summary>
/// How this invocation talks: resolved once from the global options and the environment,
/// then read everywhere instead of each command re-deciding.
/// </summary>
public sealed record OutputSettings
{
    public required OutputFormat Format { get; init; }

    /// <summary>Colour is off whenever stdout is redirected, NO_COLOR is set, or --no-color was passed.</summary>
    public required bool Color { get; init; }

    /// <summary>True when stdin is a terminal, so a prompt would actually reach someone.</summary>
    public required bool Interactive { get; init; }

    /// <summary>Suppresses progress and advisory notes on stderr. Never suppresses errors.</summary>
    public required bool Quiet { get; init; }

    /// <summary>Top-level fields to keep in JSON output, or null for all of them.</summary>
    public IReadOnlyList<string>? Fields { get; init; }

    public bool IsJson => Format == OutputFormat.Json;

    /// <summary>
    /// Resolves <see cref="OutputFormat.Auto"/> the way every well-behaved CLI does: a
    /// terminal gets the pretty rendering, a pipe gets the parseable one. An agent
    /// shelling out therefore gets JSON without having to know to ask for it.
    /// </summary>
    public static OutputSettings Resolve(
        OutputFormat requested, bool noColor, bool quiet, IReadOnlyList<string>? fields)
    {
        var stdoutRedirected = Console.IsOutputRedirected;

        var format = requested switch
        {
            OutputFormat.Auto => stdoutRedirected ? OutputFormat.Json : OutputFormat.Text,
            _ => requested
        };

        // https://no-color.org: any value at all, including empty, means no colour.
        var noColorEnv = Environment.GetEnvironmentVariable("NO_COLOR") is not null;

        return new OutputSettings
        {
            Format = format,
            Color = !noColor && !noColorEnv && !stdoutRedirected && format == OutputFormat.Text,
            Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected,
            Quiet = quiet,
            Fields = fields
        };
    }
}

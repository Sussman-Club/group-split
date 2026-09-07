using System.ComponentModel;

namespace GroupSplit.Cli.Output;

/// <summary>
/// The member descriptions are user-facing: a shell shows them beside each value when
/// completing --output, so they are worded for someone choosing one at the prompt.
/// </summary>
public enum OutputFormat
{
    [Description("Text when stdout is a terminal, JSON when it is not.")]
    Auto,

    [Description("Tables and colour, sized to the terminal. For people.")]
    Text,

    [Description("One JSON document on stdout. For scripts, pipelines and agents.")]
    Json
}

namespace GroupSplit.Cli.Output;

public enum OutputFormat
{
    /// <summary>Text when stdout is a terminal, JSON when it is not.</summary>
    Auto,

    /// <summary>Tables and colour, sized to the terminal. For people.</summary>
    Text,

    /// <summary>One JSON document on stdout. For scripts, pipelines and agents.</summary>
    Json
}

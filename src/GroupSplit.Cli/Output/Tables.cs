using Spectre.Console;

namespace GroupSplit.Cli.Output;

/// <summary>
/// Shared table shapes, so every command's text mode looks like it came from the same
/// program. Only ever reached in text mode -- JSON callers never touch this.
/// </summary>
public static class Tables
{
    public static Table Grid(params string[] headers)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);

        foreach (var header in headers)
        {
            table.AddColumn($"[bold]{Markup.Escape(header)}[/]");
        }

        return table;
    }

    public static Table KeyValue()
        => new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("key").PadRight(2))
            .AddColumn("value");

    /// <summary>Red when you owe, green when you are owed, so the sign is readable at a glance.</summary>
    public static string Money(decimal amount)
    {
        var text = Markup.Escape(amount.ToString("N2"));

        return amount switch
        {
            < 0 => $"[red]{text}[/]",
            > 0 => $"[green]{text}[/]",
            _ => $"[grey]{text}[/]"
        };
    }

    public static string Empty(string what) => $"[grey]No {Markup.Escape(what)}.[/]";
}

using GroupSplit.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Output;

/// <summary>
/// Terminal mode. Spectre renders the result on stdout; every other word goes to a second
/// console bound to stderr, so `groupsplit groups list > groups.txt` still shows its
/// warnings on screen and still writes a clean file.
/// </summary>
public sealed class TextOutputWriter : IOutputWriter
{
    private readonly IAnsiConsole _out;
    private readonly IAnsiConsole _error;

    public TextOutputWriter(OutputSettings settings, TextWriter stdout, TextWriter stderr)
    {
        Settings = settings;
        _out = CreateConsole(stdout, settings.Color);
        _error = CreateConsole(stderr, settings.Color);
    }

    public OutputSettings Settings { get; }

    public IAnsiConsole Console => _out;

    public void Write<T>(T payload, Func<T, IRenderable> renderer) => _out.Write(renderer(payload));

    public void WriteMessage(string message, object? payload = null)
        => _out.MarkupLine(Markup.Escape(message));

    public void WriteConfirmationRequest(ConfirmationRequest request)
    {
        _out.MarkupLine($"[yellow]{Markup.Escape(request.Summary)}[/]");

        foreach (var change in request.Changes)
        {
            _out.MarkupLine($"  [grey]-[/] {Markup.Escape(change)}");
        }

        _out.WriteLine();
        _out.MarkupLine("Re-run with [bold]--yes[/] to confirm:");
        _out.MarkupLine($"  [cyan]{Markup.Escape(request.ConfirmCommand)}[/]");
    }

    public void WriteError(CliError error)
    {
        _error.MarkupLine($"[red]error:[/] {Markup.Escape(error.Message)}");

        if (error.Details is { Count: > 0 })
        {
            foreach (var detail in error.Details)
            {
                _error.MarkupLine($"  [grey]-[/] {Markup.Escape(detail)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(error.Remediation))
        {
            _error.MarkupLine($"[grey]hint:[/] {Markup.Escape(error.Remediation)}");
        }
    }

    public void Note(string message)
    {
        if (!Settings.Quiet)
        {
            _error.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        }
    }

    public void Warn(string message)
    {
        if (!Settings.Quiet)
        {
            _error.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");
        }
    }

    private static IAnsiConsole CreateConsole(TextWriter writer, bool color) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = color ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = color ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });
}

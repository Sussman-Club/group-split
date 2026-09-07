using GroupSplit.Cli.Infrastructure;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Output;

/// <summary>
/// The one place that decides what reaches stdout and what reaches stderr.
/// <para>
/// The contract every command relies on: stdout carries the result and nothing else, so
/// exit code 0 means stdout is safe to parse. Progress, warnings and errors go to stderr,
/// where they never corrupt a pipe.
/// </para>
/// </summary>
public interface IOutputWriter
{
    OutputSettings Settings { get; }

    /// <summary>
    /// Writes the result. <paramref name="payload"/> is what JSON callers get, verbatim;
    /// <paramref name="renderer"/> is only consulted for a terminal. Passing the same
    /// object to both is what keeps the two renderings from drifting apart.
    /// </summary>
    void Write<T>(T payload, Func<T, IRenderable> renderer);

    /// <summary>Writes a result that has no interesting body, e.g. a delete.</summary>
    void WriteMessage(string message, object? payload = null);

    /// <summary>Writes the confirmation envelope to stdout. Caller returns exit code 4.</summary>
    void WriteConfirmationRequest(ConfirmationRequest request);

    /// <summary>Writes the error envelope to stderr.</summary>
    void WriteError(CliError error);

    /// <summary>Advisory line on stderr. Dropped under --quiet.</summary>
    void Note(string message);

    /// <summary>Warning on stderr. Dropped under --quiet.</summary>
    void Warn(string message);
}

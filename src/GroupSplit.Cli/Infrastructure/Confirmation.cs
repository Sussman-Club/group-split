using System.CommandLine;
using GroupSplit.Cli.Output;
using Spectre.Console;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The gate every destructive command passes through.
/// <para>
/// A terminal gets a prompt. Everything else -- a pipe, CI, an agent -- gets exit code 4
/// and a machine-readable description of what would have happened, including the exact
/// command that would do it. The alternative, hanging on a prompt nobody will answer, is
/// the failure mode that makes CLIs unusable from a script.
/// </para>
/// <para>
/// That command is never the caller's to write: it is read back out of the invocation by
/// <see cref="ConfirmCommand"/>, so it carries every option this one was given.
/// </para>
/// </summary>
public static class Confirmation
{
    public static void Require(
        CliContext context,
        string action,
        string summary,
        IReadOnlyList<string> changes,
        IReadOnlyDictionary<Option, string>? pinned = null)
    {
        if (context.Confirmed)
        {
            return;
        }

        var request = new ConfirmationRequest(
            action, summary, changes, ConfirmCommand.From(context.ParseResult, pinned));

        if (context.Output is TextOutputWriter text && context.Output.Settings.Interactive)
        {
            text.WriteConfirmationRequest(request);
            text.Console.WriteLine();

            if (text.Console.Confirm("Proceed?", defaultValue: false))
            {
                return;
            }

            // Non-zero: the operation did not happen, and a script that treated a
            // decline as success would carry on as if it had.
            throw new CliException(
                new CliError("Cancelled.", ErrorCodes.Cancelled),
                ExitCodes.Error);
        }

        throw new ConfirmationRequiredException(request);
    }
}

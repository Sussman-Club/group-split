using System.CommandLine;
using GroupSplit.Cli.Api;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Output;

namespace GroupSplit.Cli.Infrastructure;

public static class CommandExtensions
{
    /// <summary>
    /// Registers an action and wraps it in the one error boundary this CLI has. Every
    /// failure leaves through here, so the envelope and the exit code are decided in a
    /// single place instead of being re-derived by each command.
    /// </summary>
    public static void SetHandler(this Command command, Func<CliContext, CancellationToken, Task<int>> handler)
    {
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var settings = GlobalOptions.ReadOutputSettings(parseResult);

            IOutputWriter output = settings.IsJson
                ? new JsonOutputWriter(settings, Console.Out, Console.Error)
                : new TextOutputWriter(settings, Console.Out, Console.Error);

            using var context = new CliContext(parseResult, output);

            try
            {
                return await handler(context, ct);
            }
            catch (ConfirmationRequiredException confirmation)
            {
                // stdout, not stderr: this is the command's result, and the caller is
                // meant to read it and decide.
                output.WriteConfirmationRequest(confirmation.Request);
                return ExitCodes.ConfirmationRequired;
            }
            catch (CliException cli)
            {
                output.WriteError(cli.Error);
                return cli.ExitCode;
            }
            catch (ApiException api)
            {
                var mapped = ApiErrorMapper.Map(api);
                output.WriteError(mapped.Error);
                return mapped.ExitCode;
            }
            catch (HttpRequestException http)
            {
                output.WriteError(new CliError(
                    $"Could not reach the server: {http.Message}",
                    ErrorCodes.ServerUnreachable,
                    "Check the server URL with: groupsplit config list"));

                return ExitCodes.Error;
            }
            catch (OperationCanceledException)
            {
                output.WriteError(new CliError("Cancelled.", ErrorCodes.Cancelled));
                return ExitCodes.Error;
            }
            catch (Exception unexpected)
            {
                // Anything reaching here is a defect, so it says so rather than dressing
                // itself up as a user error -- and it prints the type, which is the part
                // that makes a bug report actionable.
                output.WriteError(new CliError(
                    $"{unexpected.GetType().Name}: {unexpected.Message}",
                    ErrorCodes.Internal,
                    $"This is a bug in the CLI. Set {EnvironmentVariables.Debug}=1 for a stack trace.",
                    Environment.GetEnvironmentVariable(EnvironmentVariables.Debug) is null
                        ? null
                        : [unexpected.ToString()]));

                return ExitCodes.Error;
            }
        });
    }
}

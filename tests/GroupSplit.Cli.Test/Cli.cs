using System.Text.Json;

namespace GroupSplit.Cli.Test;

/// <summary>What one invocation produced. The three things the CLI actually promises.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>stdout parsed as JSON. Fails the test if it is not parseable, which is the point.</summary>
    public JsonElement Json => JsonDocument.Parse(Stdout).RootElement;

    /// <summary>
    /// The error envelope. Taken from the last JSON line rather than from the whole of
    /// stderr, because stderr is the diagnostic channel: a command may have written
    /// progress there first, and `auth login` always does.
    /// </summary>
    public JsonElement Error
    {
        get
        {
            var line = Stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(candidate => candidate.StartsWith('{'));

            Assert.NotNull(line);

            return JsonDocument.Parse(line).RootElement;
        }
    }
}

public static class Cli
{
    /// <summary>
    /// Runs the real command tree with captured writers. Nothing is stubbed between here and
    /// the HTTP call, so a test asserts the contract a caller sees rather than an internal one.
    /// </summary>
    public static async Task<CliResult> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(args, stdout, stderr);

        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }
}

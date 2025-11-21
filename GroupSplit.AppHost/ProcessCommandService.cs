using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.AppHost;

public record RunProcessResult(int ExitCode);

public record RunProcessAndCaptureOutputResult(int ExitCode, string StdOut = "", string StdErr = "")
    : RunProcessResult(ExitCode);

public record ProcessRunOptions(
    Action<string>? OnStdOut,
    Action<string>? OnStdErr,
    bool KillOnCancel = true
);

public interface IProcessCommandService
{
    Task<int> RunProcessAsync(string fileName, string? workingDirectory, ICollection<string>? arguments,
        IDictionary<string, string?>? environment, ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default);
}

public class ProcessCommandService : IProcessCommandService
{
    public async Task<int> RunProcessAsync(string fileName, string? workingDirectory, ICollection<string>? arguments,
        IDictionary<string, string?>? environment, ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        workingDirectory ??= Directory.GetCurrentDirectory();
        arguments ??= Array.Empty<string>();
        environment ??= new Dictionary<string, string?>();

        using var process = new Process();

        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        foreach (var (key, value) in environment)
            process.StartInfo.EnvironmentVariables[key] = value;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                options?.OnStdOut?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                options?.OnStdErr?.Invoke(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            return -1;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (options?.KillOnCancel == true) process.Kill();
            throw;
        }

        return process.ExitCode;
    }
}

internal static class ProcessCommandServiceExtensions
{
    extension(IProcessCommandService processCommandService)
    {
        public async Task<RunProcessResult> RunProcessAndLogOutputAsync(string fileName,
            string? workingDirectory = null,
            ICollection<string>? arguments = null,
            IDictionary<string, string?>? environment = null,
            ILogger? logger = null,
            bool killOnCancel = true,
            CancellationToken cancellationToken = default)
        {
            logger ??= NullLogger.Instance;

            var exitCode = await processCommandService.RunProcessAsync(
                fileName,
                workingDirectory,
                arguments,
                environment,
                new ProcessRunOptions(
                    OnStdOut: s =>
                    {
                        if (logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation("{Data}", s);
                    },
                    OnStdErr: s =>
                    {
                        if (logger.IsEnabled(LogLevel.Error))
                            logger.LogError("{Data}", s);
                    },
                    KillOnCancel: killOnCancel
                ),
                cancellationToken
            );

            return new RunProcessResult(exitCode);
        }

        public async Task<RunProcessAndCaptureOutputResult> RunProcessAndCaptureOutputAsync(string fileName,
            string? workingDirectory = null,
            ICollection<string>? arguments = null,
            IDictionary<string, string?>? environment = null,
            bool killOnCancel = true,
            CancellationToken cancellationToken = default)
        {
            var stdOut = new StringWriter();
            var stdErr = new StringWriter();

            var exitCode = await processCommandService.RunProcessAsync(
                fileName,
                workingDirectory,
                arguments,
                environment,
                new ProcessRunOptions(
                    OnStdOut: s => stdOut.WriteLine(s),
                    OnStdErr: s => stdErr.WriteLine(s),
                    KillOnCancel: killOnCancel
                ),
                cancellationToken
            );

            return new RunProcessAndCaptureOutputResult(
                exitCode,
                stdOut.ToString(),
                stdErr.ToString()
            );
        }
    }
}
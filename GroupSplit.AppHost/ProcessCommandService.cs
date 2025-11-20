using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public record RunProcessResult(int ExitCode);

public record RunProcessAndCaptureOutputResult(int ExitCode, string StdOut = "", string StdErr = "");

public interface IProcessCommandService
{
    Task<RunProcessResult> RunProcessAsync(string fileName,
        string? workingDirectory = null, ICollection<string>? arguments = null,
        IDictionary<string, string?>? environment = null, ILogger? logger = null,
        CancellationToken cancellationToken = default);

    Task<RunProcessAndCaptureOutputResult> RunProcessAndCaptureAsync(
        string fileName,
        string? workingDirectory = null,
        ICollection<string>? arguments = null,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default);
}

public class ProcessCommandService(ILogger<ProcessCommandService> defaultLogger) : IProcessCommandService
{
    private record ProcessRunOptions(
        Action<string>? OnStdOut,
        Action<string>? OnStdErr
    );

    private async Task<int> RunInternalAsync(
        string fileName,
        string? workingDirectory,
        ICollection<string>? arguments,
        IDictionary<string, string?>? environment,
        ProcessRunOptions options,
        CancellationToken cancellationToken)
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
                options.OnStdOut?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                options.OnStdErr?.Invoke(e.Data);
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

        await process.WaitForExitAsync(cancellationToken);

        return process.ExitCode;
    }

    public async Task<RunProcessResult> RunProcessAsync(
        string fileName,
        string? workingDirectory = null,
        ICollection<string>? arguments = null,
        IDictionary<string, string?>? environment = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        logger ??= defaultLogger;

        var exitCode = await RunInternalAsync(
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
                }
            ),
            cancellationToken
        );

        return new RunProcessResult(exitCode);
    }

    public async Task<RunProcessAndCaptureOutputResult> RunProcessAndCaptureAsync(
        string fileName,
        string? workingDirectory = null,
        ICollection<string>? arguments = null,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var stdOut = new StringWriter();
        var stdErr = new StringWriter();

        var exitCode = await RunInternalAsync(
            fileName,
            workingDirectory,
            arguments,
            environment,
            new ProcessRunOptions(
                OnStdOut: s => stdOut.WriteLine(s),
                OnStdErr: s => stdErr.WriteLine(s)
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
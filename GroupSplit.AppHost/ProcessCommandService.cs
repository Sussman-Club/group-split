using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public record RunProcessAndCaptureStdOutResult(int ExitCode);

public interface IProcessCommandService
{
    Task<RunProcessAndCaptureStdOutResult> RunProcessAndCaptureOutputAsync(string fileName,
        string? workingDirectory = null, ICollection<string>? arguments = null,
        IDictionary<string, string?>? environment = null, ILogger? logger = null,
        CancellationToken cancellationToken = default);
}

internal class ProcessCommandService(ILogger<ProcessCommandService> defaultLogger) : IProcessCommandService
{
    public async Task<RunProcessAndCaptureStdOutResult> RunProcessAndCaptureOutputAsync(string fileName,
        string? workingDirectory = null, ICollection<string>? arguments = null,
        IDictionary<string, string?>? environment = null, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        workingDirectory ??= Directory.GetCurrentDirectory();
        arguments ??= Array.Empty<string>();
        environment ??= new Dictionary<string, string?>();
        logger ??= defaultLogger;

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
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment)
        {
            process.StartInfo.EnvironmentVariables[key] = value;
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{Data}", e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError("{Data}", e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to start process {process}.", fileName);
            return new RunProcessAndCaptureStdOutResult(-1);
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            logger.LogDebug("Process {process} exited with code {exitCode}.", fileName, process.ExitCode);
        }

        return new RunProcessAndCaptureStdOutResult(process.ExitCode);
    }
}
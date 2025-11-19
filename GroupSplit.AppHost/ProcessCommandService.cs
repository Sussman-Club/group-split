using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public record RunProcessAndCaptureStdOutResult(int ExitCode);

public interface IProcessCommandService
{
    Task<RunProcessAndCaptureStdOutResult> RunProcessAndCaptureOutputAsync(
        ILogger logger, string path, ICollection<string>? arguments, IDictionary<string, string?>? env,
        CancellationToken cancellationToken = default);
}

internal class ProcessCommandService : IProcessCommandService
{
    public async Task<RunProcessAndCaptureStdOutResult> RunProcessAndCaptureOutputAsync(
        ILogger logger, string path, ICollection<string>? arguments = null, IDictionary<string, string?>? env = null,
        CancellationToken cancellationToken = default)
    {
        arguments ??= Array.Empty<string>();
        env ??= new Dictionary<string, string?>();
        
        using var process = new Process();

        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env)
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
            logger.LogDebug(ex, "Failed to start process {process}.", path);
            return new RunProcessAndCaptureStdOutResult(-1);
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            logger.LogDebug("Process {process} exited with code {exitCode}.", path, process.ExitCode);
        }

        return new RunProcessAndCaptureStdOutResult(process.ExitCode);
    }
}
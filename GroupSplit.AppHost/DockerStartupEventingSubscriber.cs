using System.Diagnostics;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

namespace GroupSplit.AppHost;

/// <summary>
/// Eventing subscriber that ensures Docker is running before starting any container resources.
/// </summary>
internal sealed class DockerStartupEventingSubscriber : IDistributedApplicationEventingSubscriber
{
    public async Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
    {
        eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
        {
            // Check if there are any container resources that require Docker
            var hasContainerResources = @event.Model.Resources.Any(r =>
                r is ContainerResource ||
                r.GetType().Name.Contains("Postgres") ||
                r.GetType().Name.Contains("Redis") ||
                r.GetType().Name.Contains("Sql"));

            if (!hasContainerResources)
            {
                return;
            }

            if (await IsDockerRunningAsync(ct))
            {
                Console.WriteLine("Docker is already running.");
                return;
            }

            Console.WriteLine("Docker is not running. Attempting to start Docker Desktop...");

            if (!await TryStartDockerAsync(ct))
            {
                throw new InvalidOperationException(
                    "Failed to start Docker Desktop. Please start it manually and try again.");
            }

            Console.WriteLine("Docker Desktop started successfully. Waiting for Docker engine to be ready...");

            if (!await WaitForDockerToBeReadyAsync(timeoutSeconds: 60, ct))
            {
                throw new InvalidOperationException(
                    "Docker Desktop started but the engine did not become ready in time. Please check Docker Desktop and try again.");
            }

            Console.WriteLine("Docker engine is ready.");
        });

        await Task.CompletedTask;
    }

    private static async Task<bool> IsDockerRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Task<bool> TryStartDockerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = GetDockerDesktopStartInfo();
            if (startInfo == null)
            {
                Console.WriteLine("Could not determine how to start Docker Desktop on this platform.");
                return Task.FromResult(false);
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                return Task.FromResult(false);
            }

            // Don't wait for the process to exit, as Docker Desktop runs in the background
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start Docker Desktop: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private static ProcessStartInfo? GetDockerDesktopStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            // Try to find Docker Desktop executable first
            var dockerPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Docker", "Docker", "Docker Desktop.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Docker", "Docker", "Docker Desktop.exe")
            };

            foreach (var dockerPath in dockerPaths)
            {
                if (File.Exists(dockerPath))
                {
                    return new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-Command \"Start-Process '{dockerPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
            }

            // Fallback: try using PowerShell to start by name
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-Command \"Start-Process 'Docker Desktop'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "-a Docker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "start docker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        return null;
    }

    private static async Task<bool> WaitForDockerToBeReadyAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var endTime = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
        {
            if (await IsDockerRunningAsync(cancellationToken))
            {
                return true;
            }

            Console.WriteLine("Waiting for Docker engine to start...");
            await Task.Delay(2000, cancellationToken);
        }

        return false;
    }
}

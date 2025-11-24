using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public class DockerRunner(IProcessCommandService processCommandService, ILogger<DockerRunner> logger)
{
    /// <summary>
    ///     Ensures Docker Engine is running and ready.
    ///     Automatically starts Docker Desktop if not running and waits for it to be ready.
    /// </summary>
    public async Task EnsureDockerIsRunningAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Checking Docker status...");

        // Check if Docker is already running
        if (await IsDockerReadyAsync(cancellationToken))
        {
            logger.LogInformation("Docker is already running and ready.");
            return;
        }

        // Start Docker if not running
        logger.LogInformation("Docker is not running. Starting Docker Desktop...");

        if (!await TryStartDockerAsync(cancellationToken))
        {
            logger.LogError(
                "Docker failed to start. Please ensure Docker Desktop is installed and try again, or run it manually.");
            return;
        }

        // Wait for Docker to be ready with timeout
        const int maxRetries = 30;
        const int delayMs = 1000;

        logger.LogInformation("Waiting for Docker engine to start...");

        for (var retry = 1; retry <= maxRetries; retry++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Docker startup wait was cancelled. Please ensure Docker Desktop is installed and try again, or run it manually.");
                return;
            }

            if (await IsDockerReadyAsync(cancellationToken))
            {
                logger.LogInformation("Docker is ready.");
                return;
            }

            logger.LogDebug("Retry {retry}/{maxRetries}... Docker not ready yet.", retry, maxRetries);

            await Task.Delay(delayMs, CancellationToken.None);
        }

        logger.LogWarning(
            "Docker failed to start within {maxRetries} retries. Please ensure Docker Desktop is installed and try again, or run it manually.",
            maxRetries);
    }

    /// <summary>
    ///     Checks if Docker Engine is running and responsive.
    /// </summary>
    /// <returns>
    ///     A task that represents the asynchronous operation.
    ///     The task result contains <c>true</c> if Docker is running and responsive to commands; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    ///     This method runs 'docker info' to verify Docker daemon is accessible.
    /// </remarks>
    private async Task<bool> IsDockerReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var result =
                await processCommandService.RunProcessAndCaptureOutputAsync("docker", arguments: ["info"],
                    cancellationToken: cts.Token);

            return result.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error checking Docker status.");
            return false;
        }
    }

    private async Task<bool> TryStartDockerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            return await StartDockerAsync(cts.Token) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Starts Docker Desktop based on the current operating system.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This method uses platform-specific commands to start Docker:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>Windows: Launches Docker Desktop.exe from Program Files or LocalAppData</description>
    ///         </item>
    ///         <item>
    ///             <description>macOS: Uses 'open -a Docker' to launch Docker.app</description>
    ///         </item>
    ///         <item>
    ///             <description>Linux: Uses 'sudo systemctl start docker' to start the Docker service</description>
    ///         </item>
    ///     </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the current platform is unsupported, or when Docker Desktop cannot be located on Windows.
    /// </exception>
    private async Task<int> StartDockerAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var dockerPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Docker", "Docker", "Docker Desktop.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Docker", "Docker", "Docker Desktop.exe")
            };

            var dockerPath = dockerPaths.FirstOrDefault(File.Exists);

            if (dockerPath == null)
                throw new InvalidOperationException(
                    "Docker Desktop executable not found. Please ensure Docker Desktop is installed.");

            var result =
                await processCommandService.RunProcessAndCaptureOutputAsync(dockerPath,
                    cancellationToken: cancellationToken);
            return result.ExitCode;
        }

        if (OperatingSystem.IsMacOS())
        {
            var result = await processCommandService.RunProcessAndCaptureOutputAsync("open",
                arguments: ["-a", "Docker"],
                cancellationToken: cancellationToken);
            return result.ExitCode;
        }

        if (OperatingSystem.IsLinux())
        {
            var result = await processCommandService.RunProcessAndCaptureOutputAsync("bash",
                arguments: ["-c", "sudo systemctl start docker"], cancellationToken: cancellationToken);
            return result.ExitCode;
        }

        throw new InvalidOperationException("Unsupported operating system.");
    }
}
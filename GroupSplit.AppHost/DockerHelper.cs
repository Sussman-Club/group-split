using System.Diagnostics;

namespace GroupSplit.AppHost;

/// <summary>
///     Helper class for managing Docker Engine lifecycle.
///     Ensures Docker is running and ready before container operations.
/// </summary>
internal static class DockerHelper
{
    /// <summary>
    ///     Ensures Docker Engine is running and ready.
    ///     Automatically starts Docker Desktop if not running and waits for it to be ready.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown if Docker Desktop is not installed or fails to start within the timeout period.
    /// </exception>
    public static async Task EnsureDockerIsRunningAsync()
    {
        Console.WriteLine("Checking Docker status...");

        // Check if Docker is already running
        if (await IsDockerReadyAsync())
        {
            Console.WriteLine("Docker is already running and ready.");
            return;
        }

        // Start Docker if not running
        Console.WriteLine("Docker is not running. Starting Docker Desktop...");
        StartDocker();

        // Wait for Docker to be ready with timeout
        const int maxRetries = 60; // 60 seconds timeout
        const int delayMs = 1000; // 1 second between retries

        for (var i = 0; i < maxRetries; i++)
        {
            if (await IsDockerReadyAsync())
            {
                Console.WriteLine("Docker is ready!");
                return;
            }

            if (i == 0)
                Console.WriteLine("Waiting for Docker engine to start...");
            else if ((i + 1) % 10 == 0) Console.WriteLine($"Still waiting for Docker... ({i + 1}s)");

            await Task.Delay(delayMs);
        }

        throw new InvalidOperationException(
            $"Docker failed to start within {maxRetries} seconds. Please ensure Docker Desktop is installed and try again.");
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
    ///     Returns false if the command fails or throws any exception.
    /// </remarks>
    private static async Task<bool> IsDockerReadyAsync()
    {
        try
        {
            var exitCode = await RunProcessAsync("docker", "info");
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Starts Docker Desktop based on the current operating system.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown on Windows if Docker Desktop executable is not found in expected installation paths.
    /// </exception>
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
    ///     <para>
    ///         The process is started without waiting for completion since Docker Desktop
    ///         takes time to initialize and we'll poll for readiness separately.
    ///     </para>
    /// </remarks>
    private static void StartDocker()
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
            StartProcess(dockerPath);
        }
        else if (OperatingSystem.IsMacOS())
        {
            StartProcess("open", "-a Docker");
        }
        else // Linux
        {
            StartProcess("bash", "-c \"sudo systemctl start docker\"");
        }
    }

    /// <summary>
    ///     Runs a process asynchronously with the specified filename and arguments.
    /// </summary>
    /// <param name="fileName">The executable to run</param>
    /// <param name="arguments">Optional arguments to pass</param>
    /// <param name="redirectOutput">Whether to redirect standard output and error</param>
    /// <returns>
    ///     A task that represents the asynchronous operation.
    ///     The task result contains the exit code of the process.
    /// </returns>
    private static async Task<int> RunProcessAsync(
        string fileName,
        string? arguments = null,
        bool redirectOutput = true)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput);
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>
    ///     Starts a process asynchronously without waiting for it to complete.
    /// </summary>
    /// <param name="fileName">The executable to run</param>
    /// <param name="arguments">Optional arguments to pass</param>
    /// <remarks>
    ///     This is a fire-and-forget method used to launch processes that will run independently.
    ///     The task completes immediately after starting the process, not when the process exits.
    /// </remarks>
    private static void StartProcess(
        string fileName,
        string? arguments = null)
    {
        var process = CreateProcess(fileName, arguments, true);
        process.Start();
    }

    /// <summary>
    ///     Creates a configured Process instance ready to start.
    /// </summary>
    /// <param name="fileName">The executable to run</param>
    /// <param name="arguments">Optional arguments to pass</param>
    /// <param name="redirectOutput">Whether to redirect standard output and error</param>
    /// <returns>A configured Process instance</returns>
    private static Process CreateProcess(
        string fileName,
        string? arguments,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };

        if (!string.IsNullOrEmpty(arguments)) startInfo.Arguments = arguments;

        return new Process { StartInfo = startInfo };
    }
}
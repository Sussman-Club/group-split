using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.AppHost.Docker;

public static class DockerExtensions
{
    extension<THost>(THost host) where THost : IHost
    {
        /// <summary>
        ///     Ensures Docker Engine is running, automatically starting Docker Desktop if needed.
        ///     Waits up to 60 seconds for Docker to become ready.
        /// </summary>
        public async Task EnsureDockerIsRunning()
        {
            var cancellation = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
                new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
            var dockerRunner = ActivatorUtilities.CreateInstance<DockerRunner>(host.Services);
            await dockerRunner.EnsureDockerIsRunningAsync(cts.Token);
        }
    }
}
using System.Diagnostics;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public static class Extensions
{
    extension<TDatabaseResource>(IResourceBuilder<TDatabaseResource> dbBuilder)
        where TDatabaseResource : IResourceWithParent, IResourceWithConnectionString
    {
        /// <summary>
        /// Adds an EF Core migrator as an executable resource for the database.
        /// </summary>
        /// <param name="name">
        /// (Optional) The name of the resource. This name will be used for service discovery when referenced as a dependency.
        /// </param>
        /// <typeparam name="TMigrationsProject">
        /// The project metadata type that contains the EF Core migrations.
        /// </typeparam>
        /// <returns>
        /// A reference to the <see cref="IResourceBuilder{T}"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This resource runs <c>dotnet ef database update</c> for the specified migrations project
        /// using the database connection string.
        /// </para>
        /// <para>
        /// A health check is automatically added to the database resource. Dependent resources will wait
        /// until the migration has completed successfully.
        /// </para>
        /// </remarks>
        public IResourceBuilder<ExecutableResource> AddMigrator<TMigrationsProject>([ResourceName] string? name = null)
            where TMigrationsProject : IProjectMetadata, new()
        {
            name ??= $"migrator-{dbBuilder.Resource.Name}";

            var metadata = new TMigrationsProject();
            
            var parentBuilder = dbBuilder.ApplicationBuilder.CreateResourceBuilder(dbBuilder.Resource.Parent);

            var migrator = dbBuilder.ApplicationBuilder
                .AddExecutable(name, "dotnet", ".")
                .WithArgs(ctx =>
                {
                    ctx.Args.Add("ef");
                    ctx.Args.Add("database");
                    ctx.Args.Add("update");
                    ctx.Args.Add("--project");
                    ctx.Args.Add(metadata.ProjectPath);
                    ctx.Args.Add("--startup-project");
                    ctx.Args.Add(metadata.ProjectPath);
                    ctx.Args.Add("--verbose");
                })
                .WithEnvironment("ConnectionStrings:DefaultConnection", dbBuilder.Resource.ConnectionStringExpression)
                .WithParentRelationship(dbBuilder.Resource)
                .WaitFor(parentBuilder);

            var healthCheckName = $"{name}-health-check";

            dbBuilder.ApplicationBuilder.Services.AddHealthChecks().AddAsyncCheck(healthCheckName, _ =>
            {
                var rns = dbBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                    .GetRequiredService<ResourceNotificationService>();

                if (!rns.TryGetCurrentState(name, out var state))
                    return Task.FromResult(HealthCheckResult.Unhealthy("Migrator resource not found."));

                if (state.Snapshot.State == KnownResourceStates.Finished && state.Snapshot.ExitCode == 0)
                    return Task.FromResult(HealthCheckResult.Healthy());

                return Task.FromResult(KnownResourceStates.TerminalStates.Any(s => s == state.Snapshot.State)
                    ? HealthCheckResult.Unhealthy("Migrator finished in a terminal error state.")
                    : HealthCheckResult.Unhealthy("Migrator is still running."));
            });

            dbBuilder.WithHealthCheck(healthCheckName);

            return migrator;
        }
    }

    public static IResourceBuilder<ExecutableResource> AddEfInstaller(this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        return builder.AddExecutable(name, "dotnet", ".", "tool", "install", "--global", "dotnet-ef")
            .OnInitializeResource(async (r, e, ct) =>
            {
                var rns = e.Services.GetRequiredService<ResourceNotificationService>();
                await rns.PublishUpdateAsync(r, pre => pre with { IsHidden = true });
            });
    }

    extension<T>(IResourceBuilder<T> builder) where T : IResourceWithEndpoints
    {
        public IResourceBuilder<T> WithScalarUrl()
        {
            return builder
                .WithUrls(ctx =>
                {
                    foreach (var url in ctx.Urls.Where(x => x.Endpoint?.EndpointName is "http" or "https"))
                    {
                        url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
                    }
                })
                .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation
                {
                    Url = "/scalar/v1",
                    DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                });
        }
    }

    /// <summary>
    /// Ensures Docker Engine is running before configuring container resources.
    /// Automatically starts Docker Desktop if it's not already running.
    /// This runs synchronously during builder configuration to ensure Docker is ready
    /// before Aspire attempts to add container resources.
    /// </summary>
    public static IDistributedApplicationBuilder EnsureDockerIsRunning(
        this IDistributedApplicationBuilder builder)
    {
        // Run Docker check synchronously before any resources are added
        var startInfo = GetDockerCheckStartInfo();
        if (startInfo != null)
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Docker failed to start. Please ensure Docker Desktop is installed and try again.");
            }
        }

        return builder;
    }

    private static ProcessStartInfo? GetDockerCheckStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = """
                    -NoProfile -Command "
                    function Test-DockerReady {
                        (docker info 2>$null) -and (docker version 2>$null) -and (docker ps 2>$null)
                    }

                    if (!(Test-DockerReady)) {
                        Write-Host 'Docker is not running. Starting Docker Desktop...'
                        $dockerPaths = @(
                            \"$env:ProgramFiles\Docker\Docker\Docker Desktop.exe\",
                            \"$env:LOCALAPPDATA\Programs\Docker\Docker\Docker Desktop.exe\"
                        )
                        $dockerPath = $dockerPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
                        Start-Process $dockerPath
                    }
                    Write-Host 'Docker is ready!'
                    "
                    """,
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else // macOS and Linux
        {
            var startCommand = OperatingSystem.IsMacOS()
                ? "open -a Docker"
                : "sudo systemctl start docker";

            return new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $$"""
                    -c "
                    docker_is_ready() {
                        docker info > /dev/null 2>&1 && \
                        docker version > /dev/null 2>&1 && \
                        docker ps > /dev/null 2>&1
                    }

                    if ! docker_is_ready; then
                        echo 'Docker is not running. Starting Docker...'
                        {{startCommand}}
                        echo 'Docker starting...'
                    fi
                    "
                    """,
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
    }
    
}
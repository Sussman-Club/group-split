using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GroupSplit.AppHost;

public static class Extensions
{
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
                    DisplayText = "Scalar API",
                    DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                });
        }
    }

    extension<T>(IResourceBuilder<T> builder)
        where T : IResourceWithParent<PostgresServerResource>, IResourceWithConnectionString
    {
        public IResourceBuilder<T> WithMigrator<TMigrationsProject>() where TMigrationsProject : IProjectMetadata, new()
        {
            var metadata = new TMigrationsProject();
            const string migratorHealthCheckName = "migrator-health-check";

            var migratorCompletionSource = new TaskCompletionSource<bool>();

            var parentBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.Parent);

            parentBuilder.OnResourceReady((r, e, ct) =>
            {
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        if (await builder.Resource.GetConnectionStringAsync(ct) is { } connectionString)
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "dotnet",
                                ArgumentList =
                                {
                                    "ef",
                                    "database",
                                    "update",
                                    "--",
                                    "--ConnectionStrings:DefaultConnection",
                                    connectionString
                                },
                                WorkingDirectory = Path.GetDirectoryName(metadata.ProjectPath)
                            };

                            var process = Process.Start(psi);

                            if (process is null)
                            {
                                throw new InvalidOperationException("Failed to start process");
                            }

                            await process.WaitForExitAsync(ct);

                            if (process.ExitCode == 0)
                            {
                                migratorCompletionSource.TrySetResult(true);
                                break;
                            }
                            else
                            {
                                migratorCompletionSource.TrySetResult(false);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Connection string is null");
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                }, ct);
                
                return Task.CompletedTask;
            });

            builder.ApplicationBuilder.Services.AddHealthChecks().AddAsyncCheck(migratorHealthCheckName, async ct =>
            {
                var rns = builder.ApplicationBuilder.ExecutionContext.ServiceProvider
                    .GetRequiredService<ResourceNotificationService>();
                
                if (migratorCompletionSource.Task is { IsCompleted: true } task)
                {
                    return await task ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("Migrator failed");
                }
                
                return HealthCheckResult.Unhealthy("Migrator is not ready yet");
            });
            
            return builder.WithHealthCheck(migratorHealthCheckName);
        }
    }
}
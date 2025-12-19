using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.AppHost;

public static class Extensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDefaultServices()
        {
            services.TryAddSingleton<IProcessCommandService, ProcessCommandService>();
            return services;
        }
    }

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

    extension<TProjectResource>(IResourceBuilder<TProjectResource> resourceBuilder)
        where TProjectResource : ProjectResource
    {
        public IResourceBuilder<TProjectResource> WithDatabase(
            IResourceBuilder<IResourceWithConnectionString> dbResourceBuilder)
        {
            return resourceBuilder
                .WaitFor(dbResourceBuilder)
                .WithReference(dbResourceBuilder);
        }

        public IResourceBuilder<TProjectResource> WithProjectDefaults(ProjectResourceOptions options)
        {
            var containingType = typeof(ProjectResourceBuilderExtensions);

            var method = containingType.GetMethod(
                "WithProjectDefaults",
                BindingFlags.NonPublic | BindingFlags.Static
            );

            var genericMethod = method!.MakeGenericMethod(resourceBuilder.GetType().GenericTypeArguments[0]);

            var result = genericMethod.Invoke(null, [resourceBuilder, options]);
            return (IResourceBuilder<TProjectResource>)result!;
        }
    }
}
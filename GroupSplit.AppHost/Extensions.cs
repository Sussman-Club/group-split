using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    
    extension<TDistributedApplicationBuilder>(TDistributedApplicationBuilder builder)
        where TDistributedApplicationBuilder : IDistributedApplicationBuilder
    {
        /// <summary>
        ///     Ensures Docker Engine is running, automatically starting Docker Desktop if needed.
        ///     Waits up to 60 seconds for Docker to become ready.
        /// </summary>
        public async Task<TDistributedApplicationBuilder> EnsureDockerIsRunning()
        {
            await DockerHelper.EnsureDockerIsRunningAsync();
            return builder;
        }

        public IResourceBuilder<ExecutableResource> AddEfInstaller([ResourceName] string name)
        {
            return builder.AddExecutable(name, "dotnet", ".", "tool", "install", "--global", "dotnet-ef")
                .OnInitializeResource(async (r, e, ct) =>
                {
                    var rns = e.Services.GetRequiredService<ResourceNotificationService>();
                    await rns.PublishUpdateAsync(r, pre => pre with { IsHidden = true });
                });
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
}
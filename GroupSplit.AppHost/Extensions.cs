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
    }
}
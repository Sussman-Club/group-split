using System.Reflection;
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

        public IResourceBuilder<TProjectResource> WithScalarUrl()
        {
            return resourceBuilder
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
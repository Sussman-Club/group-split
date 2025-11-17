using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GroupSplit.AppHost;

public static class Extensions
{
    extension<TDatabaseResource>(IResourceBuilder<TDatabaseResource> dbBuilder) where TDatabaseResource : IResourceWithParent, IResourceWithConnectionString
    {
        public IResourceBuilder<ExecutableResource> AddMigrator<TMigrationsProject>()
            where TMigrationsProject : IProjectMetadata, new()
        {
            var builder = dbBuilder.ApplicationBuilder;

            var metadata = new TMigrationsProject();

            var migrator = builder
                .AddExecutable("migrator", "dotnet", ".")
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
                .WithParentRelationship(dbBuilder.Resource);
            
            const string migratorHealthCheckName = "migrator-health-check";
            
            dbBuilder.ApplicationBuilder.Services.AddHealthChecks().AddAsyncCheck(migratorHealthCheckName, async _ =>
            {
                var rns = builder.ExecutionContext.ServiceProvider.GetRequiredService<ResourceNotificationService>();
                
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(0));
                await rns.WaitForResourceAsync("migrator", KnownResourceStates.Finished, cts.Token);
                
                return HealthCheckResult.Healthy();
            });

            dbBuilder.WithHealthCheck(migratorHealthCheckName);

            return migrator;
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
                    DisplayText = "Scalar API",
                    DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                });
        }
    }
}
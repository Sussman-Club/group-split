using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
                .WithParentRelationship(dbBuilder.Resource);
            
            var healthCheckName = $"{name}-health-check";

            dbBuilder.ApplicationBuilder.Services.AddHealthChecks().AddAsyncCheck(healthCheckName, _ =>
            {
                var rns = dbBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider.GetRequiredService<ResourceNotificationService>();

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
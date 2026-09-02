using Aspire.Hosting.EntityFrameworkCore;

#pragma warning disable ASPIREDOTNETTOOL, ASPIRECSHARPAPPS001

namespace GroupSplit.AppHost.Migrations;

public static class Extensions
{
    extension(IResourceBuilder<IResourceWithConnectionString> builder)
    {
        public IResourceBuilder<EFMigrationResource> AddEFMigrations(
            [ResourceName] string name,
            string projectPath,
            string? connectionName = null,
            bool optional = false,
            string? dbContextTypeName = null,
            Action<IResourceBuilder<DotnetToolResource>>? configureToolResource = null,
            Action<IResourceBuilder<ProjectResource>>? configureProjectResource = null)
        {
            return builder.ApplicationBuilder.AddEFMigrationsProject(name, projectPath, dbContextTypeName,
                    tool =>
                    {
                        tool.WithReference(builder, connectionName,
                            optional);
                        configureToolResource?.Invoke(tool);
                    }, configureProjectResource)
                .WaitFor(builder);
        }
    }

    extension(IDistributedApplicationBuilder builder)
    {
        public IResourceBuilder<EFMigrationResource> AddEFMigrationsProject(
            [ResourceName] string name,
            string projectPath,
            string? dbContextTypeName = null,
            Action<IResourceBuilder<DotnetToolResource>>? configureToolResource = null,
            Action<IResourceBuilder<ProjectResource>>? configureProjectResource = null)
        {
            var internalResourceName = name + "-efmigrations-tool";

            // This project only exists to give EF Core a startup project to run migration commands
            // against - it is never launched, and the EF resource built from it is what the dashboard
            // and other resources interact with. Keep it out of the default resource list; it stays
            // reachable via the dashboard's "show hidden" toggle and `aspire describe --include-hidden`.
            var resourceBuilder = builder
                .AddCSharpApp(name, projectPath)
                .WithExplicitStart()
                .WithHidden();

            var migrations = dbContextTypeName is null
                ? resourceBuilder.AddEFMigrations(internalResourceName, configureToolResource)
                : resourceBuilder.AddEFMigrations(internalResourceName, dbContextTypeName, configureToolResource);

            configureProjectResource?.Invoke(resourceBuilder);

            return migrations;
        }
    }
}

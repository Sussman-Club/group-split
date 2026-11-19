using Aspire.Hosting.EntityFrameworkCore;
using Aspire.Hosting.Dotnet;

#pragma warning disable ASPIREDOTNETTOOL

namespace GroupSplit.AppHost.Extensions;

public static class EFMigrationExtensions
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
            Action<IResourceBuilder<DotnetProjectResource>>? configureProjectResource = null)
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
            Action<IResourceBuilder<DotnetProjectResource>>? configureProjectResource = null)
        {
            var internalResourceName = name + "-internal";

            // This project only exists to give EF Core a startup project to run migration commands
            // against - it is never launched, and the EF resource built from it is what the dashboard
            // and other resources interact with. Keep it out of the default resource list; it stays
            // reachable via the dashboard's "show hidden" toggle and `aspire describe --include-hidden`.
            var resourceBuilder = builder
                .AddDotnetProject(name, projectPath)
                .WithExplicitStart()
                .WithHidden();

            var migrations = dbContextTypeName is null
                ? ((IResourceBuilder<IDotnetProgramResource>)resourceBuilder).AddEFMigrations(internalResourceName, configureToolResource)
                : ((IResourceBuilder<IDotnetProgramResource>)resourceBuilder).AddEFMigrations(internalResourceName, dbContextTypeName, configureToolResource);

            configureProjectResource?.Invoke(resourceBuilder);

            return migrations;
        }
    }
}

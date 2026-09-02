using Aspire.Hosting.EntityFrameworkCore;

#pragma warning disable ASPIREDOTNETTOOL, ASPIREPROCESSCOMMAND001

namespace GroupSplit.AppHost.Migrations;

public static class Extensions
{
    private static readonly IReadOnlyCollection<string> ExcludedCommands = ["start", "stop", "restart", "rebuild"];

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
            var projectResource = CreateEFMigrationsProject(name, projectPath);
            var internalResourceName = name + "-efmigrations-tool";

            var resourceBuilder = builder.AddResource(projectResource)
                .WithExplicitStart()
                .WithProcessCommand(
                    "ef-rebuild",
                    "Rebuild",
                    "dotnet",
                    ["build", projectPath, "--no-incremental"],
                    new ProcessCommandOptions
                    {
                        IconName = "ArrowSync",
                        IconVariant = IconVariant.Regular
                    })
                .OnInitializeResource(async (r, evt, ct) =>
                {
                    foreach (var a in r.Annotations
                                 .OfType<ResourceCommandAnnotation>()
                                 .Where(x => ExcludedCommands.Contains(x.Name))
                                 .ToList())
                    {
                        r.Annotations.Remove(a);
                    }

                    await evt.Notifications.PublishUpdateAsync(r, snapshot =>
                    {
                        return snapshot with
                        {
                            Commands = [.. snapshot.Commands.Where(x => !ExcludedCommands.Contains(x.Name))]
                        };
                    });
                });

            var migrations = dbContextTypeName is null
                ? resourceBuilder.AddEFMigrations(internalResourceName, configureToolResource)
                : resourceBuilder.AddEFMigrations(internalResourceName, dbContextTypeName, configureToolResource);

            configureProjectResource?.Invoke(resourceBuilder);

            return migrations;
        }
    }

    private static ProjectResource CreateEFMigrationsProject(string name, string projectPath)
    {
        var projectResource = new ProjectResource(name);

        if (!string.IsNullOrEmpty(projectPath))
        {
            projectPath = projectPath.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            projectPath = Path.GetFullPath(projectPath);
        }

        if (Directory.Exists(projectPath))
        {
            // Path is a directory, assume it's a project directory
            var projectFiles = Directory.GetFiles(projectPath, "*.csproj", new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                RecurseSubdirectories = false,
                IgnoreInaccessible = true
            });

            if (projectFiles is [{ } projectFile])
            {
                // No project files found, just let it pass through and be handled later during resource start
                projectPath = projectFile;
            }
        }

        var projectMetadata = new EFMigrationProjectMetadata { ProjectPath = projectPath };

        projectResource.Annotations.Add(projectMetadata);

        return projectResource;
    }
}
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost.EntityFramework;

internal interface IMigrationCommand
{
    string Name { get; }
    List<string> BuildArguments(string projectPath, string? dbContextTypeName = null);
}

internal class CommandMigrationRunner(
    IProcessCommandService processCommandService,
    CommandMigratorRegistry registry,
    ILogger<CommandMigrationRunner> defaultLogger)
{
    public async Task<bool> RunAsync<TDatabaseResource>(
        IResourceBuilder<TDatabaseResource> db,
        IMigrationCommand command,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        where TDatabaseResource : IResourceWithConnectionString
    {
        logger ??= defaultLogger;

        registry.Set(db.Resource.Name, CommandMigrationState.Pending);

        var metadata = db.Resource.Annotations
            .OfType<MigrationProjectMetadataAnnotation>()
            .FirstOrDefault();

        if (metadata is null)
        {
            logger.LogError("No migration metadata found for {Db}", db.Resource.Name);
            registry.Set(db.Resource.Name, CommandMigrationState.Failed);
            return false;
        }

        try
        {
            var connectionString = await db.Resource.ConnectionStringExpression
                .GetValueAsync(cancellationToken);

            logger.LogInformation(
                "Running {Command} command for database {Db} using project {Project}",
                command.Name,
                db.Resource.Name,
                metadata.ProjectPath);

            registry.Set(db.Resource.Name, CommandMigrationState.Running);

            var result = await processCommandService.RunProcessAndLogOutputAsync(
                "dotnet",
                arguments: command.BuildArguments(metadata.ProjectPath, metadata.DbContextTypeName),
                environment: new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString
                },
                logger: logger,
                cancellationToken: cancellationToken);

            if (result.ExitCode == 0)
            {
                registry.Set(db.Resource.Name, CommandMigrationState.Succeeded);
                return true;
            }

            logger.LogError(
                "Command {Command} failed for {Db} (exit {Code}).",
                command.Name,
                db.Resource.Name,
                result.ExitCode);

            registry.Set(db.Resource.Name, CommandMigrationState.Failed);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error running {Command} command for {Db}", command.Name,
                db.Resource.Name);
            registry.Set(db.Resource.Name, CommandMigrationState.Failed);
            return false;
        }
    }
}
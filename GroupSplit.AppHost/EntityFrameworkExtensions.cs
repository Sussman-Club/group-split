using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public static class EntityFrameworkExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        public IResourceBuilder<ExecutableResource> AddEfInstaller([ResourceName] string name)
        {
            return builder.AddExecutable(name, "dotnet", ".", "tool", "install", "--global", "dotnet-ef");
        }
    }

    extension<TDatabaseResource>(IResourceBuilder<TDatabaseResource> dbResourceBuilder)
        where TDatabaseResource : IResourceWithConnectionString, IResourceWithParent
    {
        public IResourceBuilder<TDatabaseResource> WithMigrationOrchestration<TMigrationsProject>()
            where TMigrationsProject : IProjectMetadata, new()
        {
            return dbResourceBuilder
                .WithMigrationProject<TDatabaseResource, TMigrationsProject>()
                .WithResetDbCommand()
                .WithMigrateCommand()
                .AutoMigrateOnStartup();
        }

        public IResourceBuilder<TDatabaseResource> WithMigrationProject<TMigrationsProject>()
            where TMigrationsProject : IProjectMetadata, new()
        {
            return dbResourceBuilder.WithAnnotation(new MigrationProjectMetadataAnnotation<TMigrationsProject>());
        }

        public IResourceBuilder<TDatabaseResource> WithResetDbCommand(string commandName = "reset")
        {
            dbResourceBuilder.ApplicationBuilder.Services.AddCommandMigratorsServices();

            return dbResourceBuilder.WithCommand(commandName, "Reset Database",
                async context =>
                {
                    var cancellationToken = context.CancellationToken;
                    var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                        .GetLogger(dbResourceBuilder.Resource);
                    var migrationRunner = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();

                    var success = await migrationRunner.RunDropAsync(dbResourceBuilder, logger, cancellationToken) &&
                                  await migrationRunner.RunUpdateAsync(dbResourceBuilder, logger, cancellationToken);

                    return new ExecuteCommandResult { Success = success };
                }, new CommandOptions
                {
                    ConfirmationMessage =
                        "Are you sure you want to reset the database? This will drop and recreate the database.",
                    IconName = "BroomSparkle"
                });
        }

        public IResourceBuilder<TDatabaseResource> WithMigrateCommand(string commandName = "migrate")
        {
            dbResourceBuilder.ApplicationBuilder.Services.AddCommandMigratorsServices();

            return dbResourceBuilder.WithCommand(commandName, "Run EF Core migrations", async context =>
            {
                var cancellationToken = context.CancellationToken;
                var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                    .GetLogger(dbResourceBuilder.Resource);
                var migrationRunner = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();

                var success = await migrationRunner.RunUpdateAsync(dbResourceBuilder, logger, cancellationToken);

                return new ExecuteCommandResult { Success = success };
            }, new CommandOptions { IconName = "Database" });
        }

        public IResourceBuilder<TDatabaseResource> AutoMigrateOnStartup()
        {
            dbResourceBuilder.ApplicationBuilder.Services.AddCommandMigratorsServices();

            var efInstaller = dbResourceBuilder.ApplicationBuilder.Resources.OfType<ExecutableResource>()
                .First(x => x.Name == "dotnet-ef-installer");

            var efInstallerBuilder = dbResourceBuilder.ApplicationBuilder.CreateResourceBuilder(efInstaller);

            var parentBuilder =
                dbResourceBuilder.ApplicationBuilder.CreateResourceBuilder(dbResourceBuilder.Resource.Parent);

            var tcs1 = new TaskCompletionSource<(IResource, ResourceReadyEvent, CancellationToken)>();
            var tcs2 = new TaskCompletionSource<(IResource, ResourceStoppedEvent, CancellationToken)>();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(tcs1.Task, tcs2.Task);
                    var (_, e, ct) = tcs1.Task.Result;
                    var migrationRunner = e.Services.GetRequiredService<CommandMigrationRunner>();

                    var logger = e.Services
                        .GetRequiredService<IServiceProvider>()
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(dbResourceBuilder.Resource);

                    await migrationRunner.RunUpdateAsync(dbResourceBuilder, logger, ct);
                }
                catch (Exception ex)
                {
                    var registry = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                        .GetRequiredService<CommandMigratorRegistry>();

                    var logger = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                        .GetRequiredService<IServiceProvider>()
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(dbResourceBuilder.Resource);

                    logger.LogError(ex, "Error during automatic migrations on startup");

                    registry.Set(dbResourceBuilder.Resource.Name, CommandMigrationState.Idle);
                }
            });

            parentBuilder.OnResourceReady((r, e, ct) =>
            {
                tcs1.SetResult((r, e, ct));
                return Task.CompletedTask;
            });

            efInstallerBuilder.OnResourceStopped((r, e, ct) =>
            {
                if (e.ResourceEvent.Snapshot.ExitCode is 0)
                {
                    tcs2.SetResult((r, e, ct));
                    return Task.CompletedTask;
                }

                tcs2.SetException(
                    new Exception($"{r.Name} exited with code {e.ResourceEvent.Snapshot.ExitCode}"));

                return Task.CompletedTask;
            });

            dbResourceBuilder.WithCommandMigratorHealth();

            return dbResourceBuilder;
        }

        private IResourceBuilder<TDatabaseResource> WithCommandMigratorHealth(string? name = null)
        {
            name ??= $"cmd-migrator-{dbResourceBuilder.Resource.Name}";

            dbResourceBuilder.ApplicationBuilder.Services.AddCommandMigratorsServices();

            var healthCheckName = $"{name}-health-check";

            dbResourceBuilder.ApplicationBuilder.Services.AddHealthChecks().AddCheck(healthCheckName, () =>
            {
                var sp = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider;
                var registry = sp.GetRequiredService<CommandMigratorRegistry>();
                var state = registry.Get(dbResourceBuilder.Resource.Name);

                return state switch
                {
                    CommandMigrationState.Pending => HealthCheckResult.Unhealthy("Migrator pending"),
                    CommandMigrationState.Running => HealthCheckResult.Unhealthy("Migrator running"),
                    CommandMigrationState.Failed => HealthCheckResult.Unhealthy("Migrator failed"),
                    _ => HealthCheckResult.Healthy()
                };
            });

            return dbResourceBuilder.WithHealthCheck(healthCheckName);
        }
    }

    private static IServiceCollection AddCommandMigratorsServices(this IServiceCollection services)
    {
        services.TryAddSingleton<CommandMigratorRegistry>();
        services.TryAddSingleton<CommandMigrationRunner>();
        return services;
    }

    internal enum CommandMigrationState
    {
        Idle,
        Pending,
        Running,
        Succeeded,
        Failed
    }

    internal sealed class CommandMigratorRegistry
    {
        private readonly ConcurrentDictionary<string, CommandMigrationState> _states = new();

        public CommandMigrationState Get(string dbName)
        {
            return _states.GetValueOrDefault(dbName, CommandMigrationState.Pending);
        }

        public void Set(string dbName, CommandMigrationState state)
        {
            _states[dbName] = state;
        }
    }

    internal interface IMigrationCommand
    {
        string Name { get; }
        List<string> BuildArguments(string projectPath);
    }

    internal sealed class UpdateDatabaseCommand : IMigrationCommand
    {
        public string Name => "Update";

        public List<string> BuildArguments(string projectPath) =>
        [
            "ef", "database", "update",
            "--no-build",
            "--project", projectPath,
            "--startup-project", projectPath
        ];
    }

    internal sealed class DropDatabaseCommand : IMigrationCommand
    {
        public string Name => "Drop";

        public List<string> BuildArguments(string projectPath) =>
        [
            "ef", "database", "drop",
            "--force",
            "--no-build",
            "--project", projectPath,
            "--startup-project", projectPath
        ];
    }

    internal class CommandMigrationRunner(
        IProcessCommandService processCommandService,
        CommandMigratorRegistry registry,
        ILogger<CommandMigrationRunner> defaultLogger)
    {
        public Task<bool> RunUpdateAsync<TDatabaseResource>(
            IResourceBuilder<TDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
            where TDatabaseResource : IResourceWithConnectionString
            => RunAsync(db, new UpdateDatabaseCommand(), logger, cancellationToken);

        public Task<bool> RunDropAsync<TDatabaseResource>(
            IResourceBuilder<TDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
            where TDatabaseResource : IResourceWithConnectionString
            => RunAsync(db, new DropDatabaseCommand(), logger, cancellationToken);

        private async Task<bool> RunAsync<TDatabaseResource>(
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
                    arguments: command.BuildArguments(metadata.ProjectPath),
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

    public class MigrationProjectMetadataAnnotation(string projectPath) : IResourceAnnotation
    {
        public string ProjectPath => projectPath;
    }

    public sealed class MigrationProjectMetadataAnnotation<TProjectMetadata>()
        : MigrationProjectMetadataAnnotation(new TProjectMetadata().ProjectPath)
        where TProjectMetadata : IProjectMetadata, new();
}
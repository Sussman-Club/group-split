using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public static class EntityFrameworkExtensions
{
    public static IResourceBuilder<ExecutableResource> AddEfInstaller(this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        return builder.AddExecutable(name, "dotnet", ".", "tool", "install", "--global", "dotnet-ef");
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

        public IResourceBuilder<TDatabaseResource> WithResetDbCommand([ResourceName] string? commandName = null)
        {
            commandName ??= "reset";

            dbResourceBuilder.ApplicationBuilder.Services
                .AddCommandMigratorsServices()
                .TryAddSingleton<DatabaseResetService>();

            return dbResourceBuilder.WithCommand(commandName, "Reset Database",
                async context =>
                {
                    var cancellationToken = context.CancellationToken;
                    var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                        .GetLogger(dbResourceBuilder.Resource);
                    var dbResetService = context.ServiceProvider.GetRequiredService<DatabaseResetService>();
                    var migrationRunner = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();

                    var success =
                        await dbResetService.ResetDatabaseAsync(dbResourceBuilder, logger, cancellationToken)
                        && await migrationRunner.RunAsync(dbResourceBuilder, logger, cancellationToken);

                    return new ExecuteCommandResult { Success = success };
                }, new CommandOptions
                {
                    ConfirmationMessage =
                        "Are you sure you want to reset the database? This will drop and recreate the database.",
                    IconName = "BroomSparkle"
                });
        }

        public IResourceBuilder<TDatabaseResource> WithMigrateCommand([ResourceName] string? commandName = null)
        {
            commandName ??= "migrate";

            dbResourceBuilder.ApplicationBuilder.Services.AddCommandMigratorsServices();

            return dbResourceBuilder.WithCommand(commandName, "Run EF Core migrations", async context =>
            {
                var cancellationToken = context.CancellationToken;
                var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                    .GetLogger(dbResourceBuilder.Resource);
                var migrationRunner = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();

                var success = await migrationRunner.RunAsync(dbResourceBuilder, logger, cancellationToken);

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

                    await migrationRunner.RunAsync(dbResourceBuilder, logger, ct);
                }
                catch (Exception ex)
                {
                    var registry = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                        .GetRequiredService<CommandMigratorRegistry>();

                    var logger = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                        .GetRequiredService<IServiceProvider>()
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(dbResourceBuilder.Resource);

                    logger.LogError(ex, "💥 Error during automatic migrations on startup");

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

        private IResourceBuilder<TDatabaseResource> WithCommandMigratorHealth([ResourceName] string? name = null)
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

    internal class CommandMigrationRunner(
        IProcessCommandService processCommandService,
        CommandMigratorRegistry registry,
        ILogger<CommandMigrationRunner> defaultLogger)
    {
        public async Task<bool> RunAsync<TDatabaseResource>(
            IResourceBuilder<TDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default) where TDatabaseResource : IResourceWithConnectionString
        {
            logger ??= defaultLogger;

            registry.Set(db.Resource.Name, CommandMigrationState.Pending);

            var metadata = db.Resource.Annotations.OfType<MigrationProjectMetadataAnnotation>().FirstOrDefault();

            if (metadata is null)
            {
                logger.LogError("💥 No migration project metadata found for database {Db}", db.Resource.Name);
                registry.Set(db.Resource.Name, CommandMigrationState.Failed);
                return false;
            }

            try
            {
                var cs = await db.Resource.ConnectionStringExpression.GetValueAsync(cancellationToken);

                logger.LogInformation("🚀 Running migrations for database {Db} using project {Project}",
                    db.Resource.Name,
                    metadata.ProjectPath);
                registry.Set(db.Resource.Name, CommandMigrationState.Running);

                var result = await processCommandService.RunProcessAndCaptureOutputAsync(
                    logger, "dotnet",
                    new List<string>
                    {
                        "ef", "database", "update",
                        "--project", metadata.ProjectPath,
                        "--startup-project", metadata.ProjectPath,
                        "--verbose"
                    }, new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = cs
                    },
                    cancellationToken);

                if (result.ExitCode == 0)
                {
                    registry.Set(db.Resource.Name, CommandMigrationState.Succeeded);
                    return true;
                }

                logger.LogError("💥 Migrations failed (exit {Code}).", result.ExitCode);
                registry.Set(db.Resource.Name, CommandMigrationState.Failed);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "💥 Error running migrations");
                registry.Set(db.Resource.Name, CommandMigrationState.Failed);
                return false;
            }
        }
    }

    internal class DatabaseResetService(
        IProcessCommandService processCommandService,
        CommandMigratorRegistry registry,
        ILogger<DatabaseResetService> defaultLogger)
    {
        public async Task<bool> ResetDatabaseAsync<TDatabaseResource>(
            IResourceBuilder<TDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
            where TDatabaseResource : IResourceWithConnectionString
        {
            logger ??= defaultLogger;

            var metadata = db.Resource.Annotations.OfType<MigrationProjectMetadataAnnotation>().FirstOrDefault();

            if (metadata is null)
            {
                logger.LogError("💥 No migration project metadata found for database {Db}", db.Resource.Name);
                registry.Set(db.Resource.Name, CommandMigrationState.Failed);
                return false;
            }

            try
            {
                var cs = await db.Resource.ConnectionStringExpression.GetValueAsync(cancellationToken);

                logger.LogInformation(
                    "♻️ Resetting database {Db} using migrations project {Project}",
                    db.Resource.Name,
                    metadata.ProjectPath);

                registry.Set(db.Resource.Name, CommandMigrationState.Running);

                logger.LogInformation("🗑️ Dropping database {Db} via dotnet-ef", db.Resource.Name);

                var dropResult = await processCommandService.RunProcessAndCaptureOutputAsync(
                    logger,
                    "dotnet",
                    new List<string>
                    {
                        "ef", "database", "drop",
                        "--force",
                        "--no-build",
                        "--project", metadata.ProjectPath,
                        "--startup-project", metadata.ProjectPath,
                        "--verbose"
                    },
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = cs
                    },
                    cancellationToken);

                if (dropResult.ExitCode != 0)
                {
                    logger.LogError("💥 dotnet-ef drop failed (exit {Code}).", dropResult.ExitCode);
                    registry.Set(db.Resource.Name, CommandMigrationState.Failed);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "💥 Unexpected error resetting database {Db}", db.Resource.Name);
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
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GroupSplit.AppHost;

public static class EntityFrameworkExtensions
{
    public static IResourceBuilder<ExecutableResource> AddEfInstaller(this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        return builder.AddExecutable(name, "dotnet", ".", "tool", "install", "--global", "dotnet-ef");
    }

    extension(IResourceBuilder<PostgresDatabaseResource> db)
    {
        public IResourceBuilder<PostgresDatabaseResource> WithMigrationProject<TMigrationsProject>()
            where TMigrationsProject : IProjectMetadata, new()
        {
            return db.WithAnnotation(new MigrationProjectMetadataAnnotation<TMigrationsProject>());
        }

        public IResourceBuilder<PostgresDatabaseResource> WithResetDbCommand()
        {
            db.ApplicationBuilder.Services.TryAddSingleton<DatabaseResetService>();
            db.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

            return db.WithCommand("reset", "Reset Database",
                async context =>
                {
                    var dbResetService = context.ServiceProvider.GetRequiredService<DatabaseResetService>();

                    var logger = context.ServiceProvider
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(db.Resource);

                    await dbResetService.ResetDatabaseAsync(db, logger);

                    var migrator = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();
                    await migrator.RunAsync(db, logger, context.CancellationToken);

                    return new ExecuteCommandResult { Success = true };
                }, new CommandOptions
                {
                    ConfirmationMessage =
                        "Are you sure you want to reset the database? This will drop and recreate the database.",
                    IconName = "BroomSparkle"
                });
        }

        public IResourceBuilder<PostgresDatabaseResource> AutoMigrateOnStartup()
        {
            db.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

            var efInstaller = db.ApplicationBuilder.Resources.OfType<ExecutableResource>()
                .First(x => x.Name == "dotnet-ef-installer");

            var efInstallerBuilder = db.ApplicationBuilder.CreateResourceBuilder(efInstaller);

            var parentBuilder = db.ApplicationBuilder.CreateResourceBuilder(db.Resource.Parent);

            var tcs1 = new TaskCompletionSource<(IResource, ResourceReadyEvent, CancellationToken)>();
            var tcs2 = new TaskCompletionSource<(IResource, ResourceStoppedEvent, CancellationToken)>();

            _ = Task.Run(async () =>
            {
                await Task.WhenAll(tcs1.Task, tcs2.Task);
                var (_, e, ct) = tcs1.Task.Result;
                var migrationRunner = e.Services.GetRequiredService<CommandMigrationRunner>();

                var logger = e.Services
                    .GetRequiredService<IServiceProvider>()
                    .GetRequiredService<ResourceLoggerService>()
                    .GetLogger(db.Resource);

                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("🚀 Auto-migrating database {Db}", db.Resource.DatabaseName);

                var ok = await migrationRunner.RunAsync(db, logger, ct);

                if (!ok && logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError("Auto-migration failed for {Db}", db.Resource.DatabaseName);
                }
            });

            parentBuilder.OnResourceReady((r, e, ct) =>
            {
                tcs1.SetResult((r, e, ct));
                return Task.CompletedTask;
            });

            efInstallerBuilder.OnResourceStopped((r, e, ct) =>
            {
                if (e.ResourceEvent.Snapshot.ExitCode != 0)
                    return Task.CompletedTask;
                tcs2.SetResult((r, e, ct));
                return Task.CompletedTask;
            });

            db.WithCommandMigratorHealth();

            return db;
        }

        public IResourceBuilder<PostgresDatabaseResource> WithCommandMigratorHealth([ResourceName] string? name = null)
        {
            name ??= $"cmd-migrator-{db.Resource.Name}";

            db.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

            var healthCheckName = $"{name}-health-check";

            db.ApplicationBuilder.Services.AddHealthChecks().AddCheck(healthCheckName, () =>
            {
                var sp = db.ApplicationBuilder.ExecutionContext.ServiceProvider;
                var registry = sp.GetRequiredService<CommandMigratorRegistry>();
                var state = registry.Get(db.Resource.DatabaseName);

                return state switch
                {
                    CommandMigrationState.Pending => HealthCheckResult.Unhealthy("Migrator pending"),
                    CommandMigrationState.Running => HealthCheckResult.Unhealthy("Migrator running"),
                    CommandMigrationState.Failed => HealthCheckResult.Unhealthy("Migrator failed"),
                    _ => HealthCheckResult.Healthy()
                };
            });

            return db.WithHealthCheck(healthCheckName);
        }

        public IResourceBuilder<PostgresDatabaseResource> WithMigrateCommand([ResourceName] string? commandName = null)
        {
            commandName ??= "migrate";

            db.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

            return db.WithCommand(commandName, "Run EF Core migrations", async context =>
            {
                var cancellationToken = context.CancellationToken;
                var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>().GetLogger(db.Resource);
                var migrationRunner = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();

                var ok = await migrationRunner.RunAsync(db, logger, cancellationToken);
                return new ExecuteCommandResult { Success = ok };
            }, new CommandOptions { IconName = "Database" });
        }
    }

    private static IServiceCollection TryAddCommandMigratorsServices(this IServiceCollection services)
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
        CommandMigratorRegistry registry,
        ILogger<CommandMigrationRunner> defaultLogger)
    {
        public async Task<bool> RunAsync(
            IResourceBuilder<PostgresDatabaseResource> db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            logger ??= defaultLogger;

            var dbName = db.Resource.DatabaseName;
            registry.Set(dbName, CommandMigrationState.Pending);

            var metadata = db.Resource.Annotations.OfType<MigrationProjectMetadataAnnotation>().FirstOrDefault();

            if (metadata is null)
            {
                logger.LogError("💥 No migration project metadata found for database {Db}", dbName);
                registry.Set(dbName, CommandMigrationState.Failed);
                return false;
            }

            try
            {
                var cs = await db.Resource.ConnectionStringExpression.GetValueAsync(cancellationToken);

                registry.Set(dbName, CommandMigrationState.Running);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList =
                    {
                        "ef", "database", "update",
                        "--project", metadata.ProjectPath,
                        "--startup-project", metadata.ProjectPath,
                        "--verbose"
                    },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Environment =
                    {
                        ["ConnectionStrings:DefaultConnection"] = cs
                    }
                };

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("▶️ Executing EF migrations for {Project}...", metadata.ProjectPath);
                }

                using var p = new System.Diagnostics.Process();
                p.StartInfo = psi;

                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null && logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("{Data}", e.Data);
                    }
                };

                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null && logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError("{Data}", e.Data);
                    }
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                await p.WaitForExitAsync(cancellationToken);

                if (p.ExitCode == 0)
                {
                    registry.Set(dbName, CommandMigrationState.Succeeded);
                    return true;
                }

                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError("💥 Migrations failed (exit {Code}).", p.ExitCode);
                }

                registry.Set(dbName, CommandMigrationState.Failed);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "💥 Error running migrations");
                registry.Set(dbName, CommandMigrationState.Failed);
                return false;
            }
        }
    }

    public class DatabaseResetService(ILogger<DatabaseResetService> defaultLogger)
    {
        public async Task ResetDatabaseAsync(
            IResourceBuilder<PostgresDatabaseResource> dbBuilder,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            logger ??= defaultLogger;
            var cs = await dbBuilder.Resource.Parent.ConnectionStringExpression
                .GetValueAsync(cancellationToken);

            var csb = new NpgsqlConnectionStringBuilder(cs);
            var dbName = dbBuilder.Resource.DatabaseName;

            logger.LogInformation("🔁 Resetting database {db}", dbName);

            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            var terminateSql = @$"
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{dbName}'
              AND pid <> pg_backend_pid();";

            await using (var cmd = new NpgsqlCommand(terminateSql, conn))
                await cmd.ExecuteNonQueryAsync(cancellationToken);

            await using (var cmd = new NpgsqlCommand(
                             $"DROP DATABASE IF EXISTS \"{dbName}\";", conn))
                await cmd.ExecuteNonQueryAsync(cancellationToken);

            await using (var cmd = new NpgsqlCommand(
                             $"CREATE DATABASE \"{dbName}\";", conn))
                await cmd.ExecuteNonQueryAsync(cancellationToken);

            logger.LogInformation("🎉 Database {db} dropped and recreated successfully", dbName);
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
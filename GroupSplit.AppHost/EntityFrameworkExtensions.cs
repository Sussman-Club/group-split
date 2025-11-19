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

    extension(IResourceBuilder<PostgresDatabaseResource> dbResourceBuilder)
    {
        public IResourceBuilder<PostgresDatabaseResource> WithMigrationProject<TMigrationsProject>()
            where TMigrationsProject : IProjectMetadata, new()
        {
            return dbResourceBuilder.WithAnnotation(new MigrationProjectMetadataAnnotation<TMigrationsProject>());
        }

        public IResourceBuilder<PostgresDatabaseResource> WithResetDbCommand([ResourceName] string? commandName = null)
        {
            commandName ??= "reset";

            dbResourceBuilder.ApplicationBuilder.Services
                .TryAddCommandMigratorsServices()
                .TryAddSingleton<DatabaseResetService>();

            return dbResourceBuilder.WithCommand(commandName, "Reset Database",
                async context =>
                {
                    var cancellationToken = context.CancellationToken;
                    var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                        .GetLogger(dbResourceBuilder.Resource);
                    var dbResetService = context.ServiceProvider.GetRequiredService<DatabaseResetService>();
                    var migrationRunner = context.ServiceProvider.GetRequiredService<CommandMigrationRunner>();

                    var success = await dbResetService.ResetDatabaseAsync(dbResourceBuilder, logger, cancellationToken)
                                  && await migrationRunner.RunAsync(dbResourceBuilder, logger, cancellationToken);

                    return new ExecuteCommandResult { Success = success };
                }, new CommandOptions
                {
                    ConfirmationMessage =
                        "Are you sure you want to reset the database? This will drop and recreate the database.",
                    IconName = "BroomSparkle"
                });
        }

        public IResourceBuilder<PostgresDatabaseResource> WithMigrateCommand([ResourceName] string? commandName = null)
        {
            commandName ??= "migrate";

            dbResourceBuilder.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

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

        public IResourceBuilder<PostgresDatabaseResource> AutoMigrateOnStartup()
        {
            dbResourceBuilder.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

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

                    registry.Set(dbResourceBuilder.Resource.DatabaseName, CommandMigrationState.Idle);
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

        private IResourceBuilder<PostgresDatabaseResource> WithCommandMigratorHealth([ResourceName] string? name = null)
        {
            name ??= $"cmd-migrator-{dbResourceBuilder.Resource.Name}";

            dbResourceBuilder.ApplicationBuilder.Services.TryAddCommandMigratorsServices();

            var healthCheckName = $"{name}-health-check";

            dbResourceBuilder.ApplicationBuilder.Services.AddHealthChecks().AddCheck(healthCheckName, () =>
            {
                var sp = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider;
                var registry = sp.GetRequiredService<CommandMigratorRegistry>();
                var state = registry.Get(dbResourceBuilder.Resource.DatabaseName);

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
        public async Task<bool> ResetDatabaseAsync(
            IResourceBuilder<PostgresDatabaseResource> dbBuilder,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            logger ??= defaultLogger;

            try
            {
                var cs = await dbBuilder.Resource.Parent.ConnectionStringExpression
                    .GetValueAsync(cancellationToken);

                var csb = new NpgsqlConnectionStringBuilder(cs);
                var dbName = dbBuilder.Resource.DatabaseName;

                logger.LogInformation("🔁 Resetting database {db}", dbName);

                await using var conn = new NpgsqlConnection(csb.ConnectionString);
                await conn.OpenAsync(cancellationToken);

                var terminateSql = $"""
                                    SELECT pg_terminate_backend(pid)
                                    FROM pg_stat_activity
                                    WHERE datname = '{dbName}'
                                    AND pid <> pg_backend_pid();
                                    """;

                await using (var cmd = new NpgsqlCommand(terminateSql, conn))
                    await cmd.ExecuteNonQueryAsync(cancellationToken);

                await using (var cmd = new NpgsqlCommand(
                                 $"DROP DATABASE IF EXISTS \"{dbName}\";", conn))
                    await cmd.ExecuteNonQueryAsync(cancellationToken);

                await using (var cmd = new NpgsqlCommand(
                                 $"CREATE DATABASE \"{dbName}\";", conn))
                    await cmd.ExecuteNonQueryAsync(cancellationToken);

                logger.LogInformation("🎉 Database {db} dropped and recreated successfully", dbName);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "💥 Error resetting database {db}", dbBuilder.Resource.DatabaseName);
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
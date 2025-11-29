using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost.EntityFramework;

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
        public IResourceBuilder<TDatabaseResource> WithMigrationOrchestration<TMigrationsProject>(
            string? dbContextTypeName = null)
            where TMigrationsProject : IProjectMetadata, new()
        {
            return dbResourceBuilder
                .WithMigrationProject<TDatabaseResource, TMigrationsProject>(dbContextTypeName)
                .WithResetDbCommand()
                .WithMigrateCommand()
                .AutoMigrateOnStartup();
        }

        public IResourceBuilder<TDatabaseResource> WithMigrationProject<TMigrationsProject>(
            string? dbContextTypeName = null)
            where TMigrationsProject : IProjectMetadata, new()
        {
            return dbResourceBuilder.WithAnnotation(
                new MigrationProjectMetadataAnnotation<TMigrationsProject>(dbContextTypeName));
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
                var state = dbResourceBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                    .GetRequiredService<CommandMigratorRegistry>()
                    .Get(dbResourceBuilder.Resource.Name);

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

    extension(IServiceCollection services)
    {
        private IServiceCollection AddCommandMigratorsServices()
        {
            services.TryAddSingleton<CommandMigratorRegistry>();
            services.TryAddSingleton<CommandMigrationRunner>();
            return services;
        }
    }
}
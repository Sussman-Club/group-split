using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

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
                var rns = dbBuilder.ApplicationBuilder.ExecutionContext.ServiceProvider
                    .GetRequiredService<ResourceNotificationService>();

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

    public static IResourceBuilder<ExecutableResource> AddEfInstaller(this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        return builder.AddExecutable(name, "dotnet", ".", "tool", "install", "--global", "dotnet-ef")
            .OnInitializeResource(async (r, e, ct) =>
            {
                var rns = e.Services.GetRequiredService<ResourceNotificationService>();
                await rns.PublishUpdateAsync(r, pre => pre with { IsHidden = true });
            });
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
                    DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                });
        }
    }

    extension(IResourceBuilder<ProjectResource> resourceBuilder)
    {
        public IResourceBuilder<ProjectResource> WithDataPopulationCommand()
        {
            return resourceBuilder.WithCommand("seed-data", "Seed database",
                async context =>
                {
                    await SeedIdentityUsersAsync(resourceBuilder, context);
                    return new ExecuteCommandResult { Success = true };
                }, new CommandOptions
                {
                    IconName = "Coffee"
                });
        }

        private static async Task SeedIdentityUsersAsync(IResourceBuilder<ProjectResource> identityApi,
            ExecuteCommandContext context)
        {
            var cancellationToken = context.CancellationToken;
            var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                .GetLogger(identityApi.Resource);

            logger.LogInformation("🔐 Starting Identity user seeding with Bogus...");

            using var httpClient = new HttpClient();

            // Resolve http endpoint dynamically
            var httpEndpoint = identityApi.GetEndpoint("http");
            var baseUrl = await httpEndpoint.GetValueAsync(cancellationToken);

            var faker = new Bogus.Faker();

            var total = 25;
            var success = 0;
            var errors = 0;

            logger.LogInformation("📦 Generating {total} fake Identity users...", total);

            for (var i = 0; i < total; i++)
            {
                try
                {
                    // Generate fake user data
                    var first = faker.Name.FirstName();
                    var last = faker.Name.LastName();
                    var email = faker.Internet.Email(first, last);

                    var payload = new
                    {
                        email,
                        password = "Password123!",
                        firstName = first,
                        lastName = last
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    // POST to your Identity API endpoint
                    var response = await httpClient.PostAsync(
                        $"{baseUrl}/users/register",
                        content,
                        cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        success++;
                        logger.LogInformation("✅ Created identity user {Email}", email);
                    }
                    else
                    {
                        errors++;
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        logger.LogWarning("⚠️ Failed creating {Email}: {Status} - {Body}",
                            email, response.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    logger.LogError("💥 Exception on user {i}: {Message}", i, ex.Message);
                }

                await Task.Delay(50, cancellationToken);
            }

            logger.LogInformation("🎉 Identity user seeding complete! {success} created, {errors} errors.",
                success, errors);
        }
    }

    extension(IResourceBuilder<PostgresDatabaseResource> resourceBuilder)
    {
        public IResourceBuilder<PostgresDatabaseResource> WithResetDbCommand()
        {
            return resourceBuilder.WithCommand("reset", "Reset Database",
                async context =>
                {
                    await ResetDatabase(resourceBuilder, context);
                    return new ExecuteCommandResult { Success = true };
                }, new CommandOptions
                {
                    IconName = "BeerMug"
                });
        }

        private async Task ResetDatabase(ExecuteCommandContext context)
        {
            var logger = context.ServiceProvider
                .GetRequiredService<ResourceLoggerService>()
                .GetLogger(resourceBuilder.Resource);

            var cancellationToken = context.CancellationToken;

            // Get the connection string managed by Aspire
            var cs = await resourceBuilder.Resource.Parent.ConnectionStringExpression.GetValueAsync(cancellationToken);
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(cs);

            // Connect to a safe database
            var databaseName = resourceBuilder.Resource.DatabaseName;

            logger.LogInformation("🔁 Resetting database {db}", databaseName);

            await using var conn = new Npgsql.NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            // 1. Terminate connections using built-in pg_terminate_backend
            var terminate = @$"
                        SELECT pg_terminate_backend(pid)
                        FROM pg_stat_activity
                        WHERE datname = '{databaseName}'
                          AND pid <> pg_backend_pid();";

            await using (var cmd = new Npgsql.NpgsqlCommand(terminate, conn))
                await cmd.ExecuteNonQueryAsync(cancellationToken);

            // 2. Drop
            await using (var cmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", conn))
                await cmd.ExecuteNonQueryAsync(cancellationToken);

            // 3. Recreate
            await using (var cmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", conn))
                await cmd.ExecuteNonQueryAsync(cancellationToken);

            logger.LogInformation("🎉 Database {db} dropped and recreated successfully", databaseName);
        }
    }
}
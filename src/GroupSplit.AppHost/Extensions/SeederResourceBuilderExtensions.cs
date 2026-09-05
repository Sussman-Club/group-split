using Aspire.Hosting.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Extensions;

#pragma warning disable ASPIREPROJECTS001

public class SeederResource(string name) : ProjectResource(name);

public static class SeederCommandNames
{
    public const string ResetAndSeed = "reset-and-seed";
}

internal static class EfCommandNames
{
    /// <summary>Drops the database and re-applies all migrations. Registered by AddEFMigrations.</summary>
    public const string DatabaseReset = "ef-database-reset";
}

public static class SeederResourceBuilderExtensions
{
    /// <summary>
    /// The bootstrap admin Keycloak creates when the app model names none. Matches the
    /// default behind <c>KC_BOOTSTRAP_ADMIN_USERNAME</c>.
    /// </summary>
    private const string DefaultKeycloakAdminUser = "admin";

    extension(IDistributedApplicationBuilder builder)
    {
        public IResourceBuilder<SeederResource> AddSeeder<TProject>([ResourceName] string name)
            where TProject : IProjectMetadata, new()
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(name);

            var options = new ProjectResourceOptions();
            var project = new SeederResource(name);

            var projectResourceBuilder = builder
                .AddResource(project)
                .WithAnnotation(new TProject())
                .WithProjectDefaults(options);

            return projectResourceBuilder
                .WithExplicitStart()
                .WithIconName("FoodGrains");
        }
    }

    extension(IResourceBuilder<SeederResource> resourceBuilder)
    {
        /// <summary>
        /// Lets the seeder create the demo accounts in the realm as well as in the database.
        /// <para>
        /// Both halves come from the same <c>users.json</c> and share one id, which is what
        /// makes signing in as a demo account land on the seeded data: the API links an
        /// account by the token's subject, so an account registered by hand -- with a subject
        /// the seed data has never heard of -- would have the API create a second account and
        /// collide on the unique email index.
        /// </para>
        /// <para>
        /// The credentials are Keycloak's own admin parameters, passed straight through, so
        /// nothing new has to be configured and nothing is committed. Run mode only: a
        /// deployment has no seeder.
        /// </para>
        /// </summary>
        public IResourceBuilder<SeederResource> WithKeycloakSeeding(
            IResourceBuilder<KeycloakResource> keycloak)
        {
            var builder = resourceBuilder
                .WithReference(keycloak)
                .WaitFor(keycloak)
                .WithEnvironment("Keycloak__AdminPassword",
                    ReferenceExpression.Create($"{keycloak.Resource.AdminPasswordParameter}"));

            // The username parameter is null unless one was passed to AddKeycloak, and this
            // model does not pass one -- so the container takes Keycloak's own default and
            // the seeder has to be told the same name. The password is always a parameter,
            // generated when it is not supplied, so it needs no such fallback.
            return keycloak.Resource.AdminUserNameParameter is { } adminUser
                ? builder.WithEnvironment("Keycloak__AdminUser",
                    ReferenceExpression.Create($"{adminUser}"))
                : builder.WithEnvironment("Keycloak__AdminUser", DefaultKeycloakAdminUser);
        }

        public IResourceBuilder<SeederResource> WithResetAndSeedCommand()
        {
            return resourceBuilder.WithCommand(
                name: SeederCommandNames.ResetAndSeed,
                displayName: "Reset databases and seed",
                executeCommand: async context =>
                {
                    var rcs = context.Services.GetRequiredService<ResourceCommandService>();
                    var model = context.Services.GetRequiredService<DistributedApplicationModel>();

                    var migrations = model.Resources.OfType<EFMigrationResource>();

                    var tasks = migrations.Select(resource =>
                        rcs.ExecuteCommandAsync(resource, EfCommandNames.DatabaseReset, context.CancellationToken));

                    foreach (var result in await Task.WhenAll(tasks))
                    {
                        if (!result.Success)
                            return result;
                    }

                    return await rcs.ExecuteCommandAsync(
                        context.ResourceName,
                        commandName: "resource-start",
                        context.CancellationToken);
                },
                new CommandOptions
                {
                    IconName = "BroomSparkle",
                    IsHighlighted = true,
                    ConfirmationMessage =
                        "This will reset all databases and reseed them. Are you sure?"
                });
        }
    }
}

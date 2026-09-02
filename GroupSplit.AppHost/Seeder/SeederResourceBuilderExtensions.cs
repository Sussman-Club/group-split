using Aspire.Hosting.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Seeder;

#pragma warning disable ASPIREPROJECTS001

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

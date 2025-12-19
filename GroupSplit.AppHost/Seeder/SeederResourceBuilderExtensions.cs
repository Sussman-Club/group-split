using GroupSplit.AppHost.EntityFramework;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Seeder;

public static class SeederCommandNames
{
    public const string ResetAndSeed = "reset-and-seed";
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
        public IResourceBuilder<SeederResource> WithDatabase(
            IResourceBuilder<IResourceWithConnectionString> dbResourceBuilder)
        {
            resourceBuilder.WithAnnotation(new DatabaseAnnotation(dbResourceBuilder.Resource));
            return Extensions.WithDatabase(resourceBuilder, dbResourceBuilder);
        }

        public IResourceBuilder<SeederResource> WithResetAndSeedCommand()
        {
            return resourceBuilder.WithCommand(
                name: SeederCommandNames.ResetAndSeed,
                displayName: "Reset databases and seed",
                executeCommand: async context =>
                {
                    var rcs = context.ServiceProvider.GetRequiredService<ResourceCommandService>();

                    var dbResources = resourceBuilder.Resource.Annotations
                        .OfType<DatabaseAnnotation>()
                        .Select(x => x.Resource);

                    var tasks = dbResources.Select(resource =>
                        rcs.ExecuteCommandAsync(resource, DatabaseCommandNames.Reset, context.CancellationToken));

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
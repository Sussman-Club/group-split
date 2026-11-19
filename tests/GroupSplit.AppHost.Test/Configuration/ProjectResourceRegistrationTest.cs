using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.EntityFrameworkCore;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Extensions;
using Projects;

namespace GroupSplit.AppHost.Test.Configuration;

public sealed class ProjectResourceRegistrationTest
{
    [Fact]
    public async Task Project_resources_and_migration_commands_are_registered()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<GroupSplit_AppHost>(cancellationToken: TestContext.Current.CancellationToken);

        var projects = new Dictionary<string, string>
        {
            ["api"] = "GroupSplit.API.csproj",
            ["mcp"] = "GroupSplit.Mcp.csproj",
            ["web"] = "GroupSplit.App.Web.csproj",
            ["realm-settings"] = "GroupSplit.RealmWorker.csproj",
            ["realm-reset"] = "GroupSplit.RealmWorker.csproj",
            ["seeder"] = "GroupSplit.Seeder.csproj",
            ["migrations"] = "GroupSplit.Data.PostgreSQL.Migrations.csproj"
        };

        foreach (var (name, expectedProject) in projects)
        {
            var project = Assert.IsType<DotnetProjectResource>(builder.Resources.Single(r => r.Name == name));
            var path = project.GetProjectMetadata().ProjectPath;
            Assert.Equal(expectedProject, Path.GetFileName(path));
            Assert.True(File.Exists(path), $"Project for {name} does not exist: {path}");
        }

        Assert.IsType<EFMigrationResource>(builder.Resources.Single(r => r.Name == "migrations-internal"));
        var seeder = builder.Resources.Single(r => r.Name == "seeder");
        Assert.Contains(seeder.Annotations.OfType<ResourceCommandAnnotation>(),
            command => command.Name == SeederCommandNames.ResetAndSeed);

        foreach (var name in new[] { "plaid-redirect-uri", "bank-key-certificate", "push-webbaseurl" })
        {
            var parameter = Assert.IsType<ParameterResource>(builder.Resources.Single(r => r.Name == name));
            Assert.False(parameter.Required, $"{name} must not block startup when omitted.");
        }
    }
}

using Aspire.Hosting;
using GroupSplit.AppHost.Extensions;

namespace GroupSplit.AppHost.Test.Configuration;

public sealed class PublishedPushConfigurationTest
{
    [Fact]
    public async Task Web_push_settings_are_sent_to_the_correct_services()
    {
        var output = Directory.CreateTempSubdirectory("groupsplit-web-push-compose-");
        try
        {
            var builder = DistributedApplication.CreateBuilder(
                ["--operation", "publish", "--step", "publish-compose", "--output-path", output.FullName]);
            builder.AddDockerComposeEnvironment("compose");
            var api = builder.AddContainer("api", "example-api");
            builder.AddContainer("web", "example-web").WithFirebaseWebPush(api);
            await using (var app = builder.Build())
            {
                await app.RunAsync(TestContext.Current.CancellationToken);
            }

            var compose = await File.ReadAllTextAsync(Path.Combine(output.FullName, "docker-compose.yaml"),
                TestContext.Current.CancellationToken);
            var web = compose[compose.IndexOf("  web:", StringComparison.Ordinal)..];
            var apiSection = compose[compose.IndexOf("  api:", StringComparison.Ordinal)..compose.IndexOf("  web:", StringComparison.Ordinal)];
            Assert.Contains("Push__WebBaseUrl", apiSection);
            Assert.DoesNotContain("WebPush__", apiSection);
            Assert.Contains("WebPush__ApiKey", web);
            Assert.Contains("WebPush__ProjectId", web);
            Assert.Contains("WebPush__MessagingSenderId", web);
            Assert.Contains("WebPush__AppId", web);
            Assert.Contains("WebPush__VapidKey", web);
            Assert.DoesNotContain("Push__WebBaseUrl", web);
        }
        finally { output.Delete(recursive: true); }
    }

    [Fact]
    public async Task Firebase_credentials_are_referenced_only_by_the_api_and_stay_out_of_the_compose_file()
    {
        var output = Directory.CreateTempSubdirectory("groupsplit-push-compose-");
        try
        {
            var builder = DistributedApplication.CreateBuilder(
                ["--operation", "publish", "--step", "publish-compose", "--output-path", output.FullName]);
            builder.Configuration["Parameters:push-enabled"] = "true";
            builder.Configuration["Parameters:push-project-id"] = "test-firebase-project";
            builder.Configuration["Parameters:firebase-service-account-base64"] = "private-test-credential";
            builder.AddDockerComposeEnvironment("compose");
            builder.AddContainer("api", "example-api").WithFirebasePush();
            builder.AddContainer("web", "example-web");
            await using (var app = builder.Build())
            {
                await app.RunAsync(TestContext.Current.CancellationToken);
            }

            var compose = await File.ReadAllTextAsync(Path.Combine(output.FullName, "docker-compose.yaml"),
                TestContext.Current.CancellationToken);
            Assert.Contains("Push__Enabled", compose);
            Assert.Contains("Push__ProjectId", compose);
            Assert.Contains("Push__ServiceAccountBase64", compose);
            Assert.DoesNotContain("private-test-credential", compose);
            Assert.Equal(1, compose.Split("Push__ServiceAccountBase64").Length - 1);
            var web = compose[compose.IndexOf("  web:", StringComparison.Ordinal)..];
            Assert.DoesNotContain("Push__", web);
        }
        finally { output.Delete(recursive: true); }
    }
}

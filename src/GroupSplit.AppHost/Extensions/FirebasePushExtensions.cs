using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

#pragma warning disable ASPIREPIPELINES001

public static class FirebasePushExtensions
{
    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        public IResourceBuilder<T> WithFirebasePush()
        {
            var builder = resource.ApplicationBuilder;
            var enabled = builder.AddOptionalParameter("push-enabled", "false");
            var project = builder.AddOptionalParameter("push-project-id");
            var credential = builder.AddOptionalParameter("firebase-service-account-base64", secret: true);
            builder.Pipeline.AddStep("validate-firebase-push",
                context => enabled.RequireValuesWhenEnabledAsync(context, [project, credential]),
                dependsOn: WellKnownPipelineSteps.ProcessParameters,
                requiredBy: WellKnownPipelineSteps.BuildPrereq);
            return resource.WithEnvironment("Push__Enabled", enabled)
                .WithEnvironment("Push__ProjectId", project)
                .WithEnvironment("Push__ServiceAccountBase64", credential);
        }
    }
}

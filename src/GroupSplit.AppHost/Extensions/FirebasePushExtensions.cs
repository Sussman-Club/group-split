using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

#pragma warning disable ASPIREPIPELINES001

public static class FirebasePushExtensions
{
    public static IResourceBuilder<T> WithFirebaseWebPush<T, TApi>(this IResourceBuilder<T> web, IResourceBuilder<TApi> api)
        where T : IResourceWithEnvironment where TApi : IResourceWithEnvironment
    {
        var builder = web.ApplicationBuilder;
        web.WithEnvironment("WebPush__ApiKey", builder.AddOptionalParameter("webpush-apikey"))
            .WithEnvironment("WebPush__ProjectId", builder.AddOptionalParameter("webpush-projectid"))
            .WithEnvironment("WebPush__MessagingSenderId", builder.AddOptionalParameter("webpush-messagingsenderid"))
            .WithEnvironment("WebPush__AppId", builder.AddOptionalParameter("webpush-appid"))
            .WithEnvironment("WebPush__VapidKey", builder.AddOptionalParameter("webpush-vapidkey", secret: true));
        api.WithEnvironment("Push__WebBaseUrl", builder.AddOptionalParameter("push-webbaseurl"));
        return web;
    }

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

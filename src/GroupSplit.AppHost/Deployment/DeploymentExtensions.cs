using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace GroupSplit.AppHost.Deployment;

#pragma warning disable ASPIREDOCKERFILEBUILDER001

/// <summary>
/// Publish-time wiring for the Docker Compose target. Everything here is deployment shaped and
/// deliberately absent from run mode, where the AppHost orchestrator handles the same concerns.
/// </summary>
public static class DeploymentExtensions
{
    /// <summary>
    /// Resolves the image the hosting integration selected, so a derived image builds
    /// <c>FROM</c> the same tag instead of one pinned here and left to drift.
    /// </summary>
    private static string BaseImageOf(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().Last();

        return image.Registry is null
            ? $"{image.Image}:{image.Tag}"
            : $"{image.Registry}/{image.Image}:{image.Tag}";
    }

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEndpoints
    {
        /// <summary>
        /// Publishes the primary HTTP endpoint on a fixed host port.
        /// <para>
        /// An unpinned endpoint lands on a random host port, and
        /// <c>WithExternalHttpEndpoints</c> would publish every HTTP endpoint, including
        /// Keycloak's management port. This exposes exactly one.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> PublishOnHostPort(int hostPort)
            => resource.WithEndpoint("http", endpoint =>
            {
                endpoint.IsExternal = true;
                endpoint.Port = hostPort;
            });
    }

    extension<T>(IResourceBuilder<T> container) where T : ContainerResource
    {
        /// <summary>
        /// Bakes SQL into the image for the Postgres entrypoint to run on first start.
        /// <para>
        /// Databases added with <c>AddDatabase</c> are created by the AppHost orchestrator,
        /// which does not run in a deployment, so they have to be created by the container
        /// itself. The documented form of this uses a bind mount, which does not survive
        /// <c>aspire publish</c>: the host path does not exist on the target machine.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> WithBakedInitScript(string scriptPath)
            => container.WithDockerfileBuilder(".", context =>
            {
                context.Builder
                    .From(BaseImageOf(context.Resource))
                    .Copy(scriptPath, $"/docker-entrypoint-initdb.d/{Path.GetFileName(scriptPath)}");
            });
    }

    extension(IResourceBuilder<DockerComposeEnvironmentResource> compose)
    {
        /// <summary>
        /// Applies the Compose defaults Aspire leaves to the operator.
        /// <para>
        /// Compose gives services no restart policy, so a crash or host reboot leaves the
        /// stack down, and <c>depends_on</c> defaults to <c>service_started</c>, which lets
        /// consumers race Postgres while it is still running its first-time init.
        /// </para>
        /// </summary>
        public IResourceBuilder<DockerComposeEnvironmentResource> WithComposeDefaults(string databaseService)
            => compose.ConfigureComposeFile(file =>
            {
                // The one-shot migration bundle sets its own "no" and keeps it.
                foreach (var service in file.Services.Values)
                {
                    service.Restart ??= "unless-stopped";
                }

                if (!file.Services.TryGetValue(databaseService, out var database))
                {
                    return;
                }

                database.Healthcheck = new Healthcheck
                {
                    Test = ["CMD-SHELL", "pg_isready -U postgres -d postgres"],
                    Interval = "5s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "10s"
                };

                foreach (var service in file.Services.Values)
                {
                    if (service.DependsOn.TryGetValue(databaseService, out var onDatabase))
                    {
                        onDatabase.Condition = "service_healthy";
                    }
                }
            });
    }

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// Points OIDC at the issuer the browser sees.
        /// <para>
        /// Tokens carry the issuer the browser was redirected to, so validating against the
        /// internal compose address would never match. Both the web app and the API refuse
        /// to start outside development without this, and each derives whether to demand
        /// HTTPS metadata from the scheme of the authority it is given.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> WithKeycloakAuthority(ReferenceExpression authority)
            => resource.WithEnvironment("Keycloak__Authority", authority);
    }
}

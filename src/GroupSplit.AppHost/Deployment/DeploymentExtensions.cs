using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace GroupSplit.AppHost.Deployment;

/// <summary>
/// Publish-time wiring for the Docker Compose target. Everything here is deployment shaped and
/// deliberately absent from run mode, where the AppHost orchestrator handles the same concerns.
/// </summary>
public static class DeploymentExtensions
{
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

    extension(IResourceBuilder<DockerComposeEnvironmentResource> compose)
    {
        /// <summary>
        /// Ships local files into a service as Compose configs, inlining their contents into
        /// the generated Compose file.
        /// <para>
        /// Both obvious alternatives fail here. A bind mount points at a host path that does
        /// not exist on the target machine. A derived image drags the entire base image back
        /// through the registry, and the 342 MB Postgres and 266 MB Keycloak layers are
        /// rejected with 413 Payload Too Large, so only the app images can be pushed.
        /// </para>
        /// <para>
        /// Text files only: content is inlined as text, so binaries would be mangled.
        /// </para>
        /// </summary>
        public IResourceBuilder<DockerComposeEnvironmentResource> WithFiles(
            string serviceName,
            string sourcePath,
            string targetPath)
        {
            var source = Path.Combine(compose.ApplicationBuilder.AppHostDirectory, sourcePath);
            var isDirectory = Directory.Exists(source);

            var files = isDirectory
                ? Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                : [source];

            return compose.ConfigureComposeFile(file =>
            {
                if (!file.Services.TryGetValue(serviceName, out var service))
                {
                    return;
                }

                foreach (var path in files)
                {
                    var relative = isDirectory
                        ? Path.GetRelativePath(source, path).Replace('\\', '/')
                        : Path.GetFileName(path);

                    var name = $"{serviceName}-{relative}".ToLowerInvariant();
                    name = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));

                    file.Configs[name] = new Config { Name = name, Content = File.ReadAllText(path) };

                    service.Configs.Add(new ConfigReference
                    {
                        Source = name,
                        Target = isDirectory ? $"{targetPath}/{relative}" : targetPath
                    });
                }
            });
        }

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

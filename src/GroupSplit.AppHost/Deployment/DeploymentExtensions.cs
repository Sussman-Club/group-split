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

    extension<T>(IResourceBuilder<T> resource) where T : ContainerResource
    {
        /// <summary>
        /// Ships local files into the container. The Docker Compose publisher turns the
        /// container-files annotation into one Compose config per file, inlining the contents
        /// into the generated Compose file.
        /// <para>
        /// Both obvious alternatives fail here. A bind mount points at a host path that does
        /// not exist on the target machine, and that is what the framework's own
        /// <c>WithContainerFiles(destination, source)</c>, <c>WithInitFiles</c> and
        /// <c>WithRealmImport</c> all degrade to outside run mode. A derived image drags the
        /// entire base image back through the registry, and the 342 MB Postgres and 266 MB
        /// Keycloak layers are rejected with 413 Payload Too Large, so only the app images
        /// can be pushed.
        /// </para>
        /// <para>
        /// Contents are passed inline rather than as a source path: a source path makes the
        /// publisher copy the file next to the Compose file and reference it with <c>file:</c>,
        /// and the deploy hands Komodo only the Compose text. That also means text files only,
        /// since binaries would be mangled.
        /// </para>
        /// <para>
        /// One call per file, each with a single flat entry, rather than one call for a whole
        /// directory: the publisher's <c>ContainerDirectory</c> recursion advances its running
        /// path once per entry, so a directory holding more than one child misplaces every
        /// child after the first.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> WithFiles(string sourcePath, string targetPath)
        {
            var source = Path.Combine(resource.ApplicationBuilder.AppHostDirectory, sourcePath);

            if (File.Exists(source))
            {
                return resource.WithContainerFiles(
                    Path.GetDirectoryName(targetPath)!.Replace('\\', '/'),
                    [new ContainerFile
                    {
                        Name = Path.GetFileName(targetPath),
                        Contents = File.ReadAllText(source)
                    }]);
            }

            foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var directory = Path.GetRelativePath(source, Path.GetDirectoryName(path)!)
                    .Replace('\\', '/');

                resource.WithContainerFiles(
                    directory == "." ? targetPath : $"{targetPath}/{directory}",
                    [new ContainerFile
                    {
                        Name = Path.GetFileName(path),
                        Contents = File.ReadAllText(path)
                    }]);
            }

            return resource;
        }
    }

    extension(IResourceBuilder<DockerComposeEnvironmentResource> compose)
    {
        /// <summary>
        /// Publishes the Aspire dashboard alongside the stack, behind a browser token.
        /// <para>
        /// The dashboard shows every environment variable of every service, secrets among
        /// them, and it is published on a host port so the operator can reach it from the
        /// LAN without a Caddy route. That is why the token is not optional here: left to
        /// itself the standalone dashboard image generates a random token and prints it to
        /// its own log, and a token nobody can read without shell access to the host is a
        /// dashboard nobody uses.
        /// </para>
        /// <para>
        /// TLS is terminated upstream when the dashboard is fronted by the proxy, so it has
        /// to learn the original scheme and host from the forwarded headers the same way
        /// Keycloak does, or its redirects point back at plain HTTP.
        /// </para>
        /// </summary>
        public IResourceBuilder<DockerComposeEnvironmentResource> WithProtectedDashboard(
            IResourceBuilder<ParameterResource> token)
            => compose.WithDashboard(dashboard => dashboard
                .WithHostPort(18888)
                .WithForwardedHeaders(enabled: true)
                .WithEnvironment("Dashboard__Frontend__AuthMode", "BrowserToken")
                .WithEnvironment("Dashboard__Frontend__BrowserToken", token));

        /// <summary>
        /// Applies the Compose defaults Aspire leaves to the operator.
        /// <para>
        /// Compose gives services no restart policy, so a crash or host reboot leaves the
        /// stack down. And <c>depends_on</c> defaults to <c>service_started</c>, which lets a
        /// consumer race a dependency that is up but not yet answering -- Postgres running its
        /// first-time init, say.
        /// </para>
        /// <para>
        /// The publisher writes every <c>WaitFor</c> out as <c>service_started</c> even when the
        /// wait asked for healthy, because at the point it runs no service has a healthcheck yet
        /// -- its own source says as much, and leaves the <c>service_healthy</c> line commented
        /// out. This is the other half: healthchecks are declared on the resources that own them
        /// (see <c>WithComposeHealthcheck</c>), and by the time the whole file is in hand every
        /// edge pointing at a service that has one can be upgraded.
        /// </para>
        /// <para>
        /// Both rules read the generated file rather than naming resources, so giving another
        /// resource a healthcheck tomorrow needs no change here.
        /// </para>
        /// </summary>
        public IResourceBuilder<DockerComposeEnvironmentResource> WithComposeDefaults()
            => compose.ConfigureComposeFile(file =>
            {
                // The one-shot migration bundle sets its own "no" and keeps it.
                foreach (var service in file.Services.Values)
                {
                    service.Restart ??= "unless-stopped";
                }

                var healthchecked = file.Services
                    .Where(entry => entry.Value.Healthcheck is not null)
                    .Select(entry => entry.Key)
                    .ToHashSet();

                foreach (var dependency in file.Services.Values
                    .SelectMany(service => service.DependsOn)
                    .Where(edge => healthchecked.Contains(edge.Key))
                    .Select(edge => edge.Value))
                {
                    // Only the default is ours to strengthen. A "run to completion" edge, which
                    // is what the migration bundle's consumers get, already says something
                    // stricter.
                    if (string.IsNullOrEmpty(dependency.Condition)
                        || dependency.Condition == "service_started")
                    {
                        dependency.Condition = "service_healthy";
                    }
                }
            });
    }

    extension<T>(IResourceBuilder<T> resource) where T : IComputeResource
    {
        /// <summary>
        /// Declares the published service's Compose healthcheck.
        /// <para>
        /// Aspire's own health checks belong to the run-mode orchestrator and are not
        /// translated into the Compose file, so a deployed stack has none until one is set
        /// here. Kept on the resource that owns it, next to the image whose probe command it
        /// has to match; <c>WithComposeDefaults</c> then makes the dependents wait for it.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> WithComposeHealthcheck(Healthcheck healthcheck)
            => resource.PublishAsDockerComposeService((_, service) => service.Healthcheck = healthcheck);

        /// <summary>
        /// Joins the published service to a network managed outside this Compose file, under a
        /// fixed container name.
        /// <para>
        /// The host publishes every stack the same way: the service that should be reachable
        /// joins the shared <c>internal</c> network, and the Caddy reverse proxy (itself on
        /// that network, fronted by the Cloudflare tunnel) routes a hostname to it. Everything
        /// else stays on the stack's own network, invisible to the proxy.
        /// </para>
        /// <para>
        /// The container name is what Caddy dials. Compose would otherwise name the container
        /// <c>group-split-web-1</c> and alias it by its bare service name -- and short names
        /// like <c>web</c> are exactly the ones a shared network ends up disputing.
        /// </para>
        /// </summary>
        public IResourceBuilder<T> WithExternalNetwork(
            IResourceBuilder<DockerComposeEnvironmentResource> compose,
            string networkName,
            string containerName)
        {
            compose.ConfigureComposeFile(file => file.AddNetwork(new Network
            {
                Name = networkName,
                External = true
            }));

            return resource.PublishAsDockerComposeService((_, service) =>
            {
                service.ContainerName = containerName;
                service.Networks.Add(networkName);
            });
        }
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

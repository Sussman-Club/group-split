// Probes and the endpoint-selector overload are still experimental API, suppressed here the
// same way AppHost.cs suppresses them.
#pragma warning disable ASPIREPROBES001

using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace GroupSplit.AppHost.Extensions;

public static class HealthEndpointExtensions
{
    /// <summary>
    /// Name of the management endpoint. Matches Keycloak's own term for the same idea.
    /// </summary>
    public const string ManagementEndpointName = "management";

    extension(IResourceBuilder<ProjectResource> project)
    {
        /// <summary>
        /// Puts the health endpoints on their own endpoint, and points the probes at it.
        /// <para>
        /// A health endpoint reports the status of every registered check, so publishing one
        /// tells anonymous callers which dependencies exist and which are down. Giving it an
        /// endpoint of its own means it can simply never be published -- nothing calls
        /// <c>PublishOnHostPort</c> for this one -- which is the same arrangement Keycloak
        /// ships with, serving /health/ready on management port 9000. That makes the endpoint
        /// unreachable by topology rather than by an environment check, so no service has to
        /// be trusted to decide whether exposing it is safe.
        /// </para>
        /// <para>
        /// The port travels to the app as configuration because the app is what has to bind
        /// the endpoints to it; the framework only hands out the port it allocated.
        /// </para>
        /// </summary>
        public IResourceBuilder<ProjectResource> WithHealthEndpoints()
            => project
                .WithHttpEndpoint(name: ManagementEndpointName)
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["HealthChecks__Port"] = project
                        .GetEndpoint(ManagementEndpointName)
                        .Property(EndpointProperty.TargetPort);
                })
                .WithHttpProbe(
                    ProbeType.Liveness,
                    () => project.GetEndpoint(ManagementEndpointName),
                    "/alive")
                .WithHttpProbe(
                    ProbeType.Readiness,
                    () => project.GetEndpoint(ManagementEndpointName),
                    "/health");

        /// <summary>
        /// The publish-mode counterpart of <see cref="WithHealthEndpoints"/>: the same
        /// readiness check, restated as a Compose healthcheck because the publisher does not
        /// translate the probes above.
        /// <para>
        /// Same shape as Keycloak's, for the same reason: the image ships no HTTP client, so
        /// bash opens a socket on the management port and speaks HTTP/1.0 across it. The port
        /// is read from <c>HealthChecks__Port</c> inside the container -- <c>$$</c> is how a
        /// Compose file spells a literal <c>$</c> -- which is the variable
        /// <see cref="WithHealthEndpoints"/> sets, so the probe and the endpoint cannot drift
        /// apart.
        /// </para>
        /// </summary>
        public IResourceBuilder<ProjectResource> WithManagementHealthcheck()
            => project.WithComposeHealthcheck(new Healthcheck
            {
                Test =
                [
                    "CMD", "bash", "-c",
                    @"{ printf 'HEAD /health HTTP/1.0\r\n\r\n' >&0; grep -q '^HTTP/1\.[01] 200'; } 0<>/dev/tcp/localhost/$$HealthChecks__Port"
                ],
                Interval = "5s",
                Timeout = "5s",
                Retries = 12,
                StartPeriod = "30s"
            });
    }
}

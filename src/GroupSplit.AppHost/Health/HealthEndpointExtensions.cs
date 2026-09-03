// Probes and the endpoint-selector overload are still experimental API, suppressed here the
// same way AppHost.cs suppresses them.
#pragma warning disable ASPIREPROBES001

namespace GroupSplit.AppHost.Health;

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
    }
}

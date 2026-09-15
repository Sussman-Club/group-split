using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace GroupSplit.AppHost.Extensions;

public static class RustFsDeploymentExtensions
{
    /// <summary>
    /// Where the wrapper entrypoint is shipped inside the storage container. Run through
    /// <c>sh</c> rather than executed directly: a Compose config arrives read-only and without
    /// the executable bit.
    /// </summary>
    private const string EntrypointPath = "/usr/local/bin/create-buckets.sh";

    /// <summary>
    /// Names the buckets for the shipped script, which reads them as a space-separated list.
    /// </summary>
    private const string BucketsVariable = "GROUPSPLIT_INIT_BUCKETS";

    /// <summary>
    /// Turns on the RustFS console. The flag itself takes no value, so the environment
    /// variable is the only way to set it.
    /// </summary>
    private const string ConsoleVariable = "RUSTFS_CONSOLE_ENABLE";

    extension(IResourceBuilder<RustFsResource> storage)
    {
        /// <summary>
        /// Prepares the RustFS server to run outside local development, creating
        /// <paramref name="buckets"/> as the container starts.
        /// <para>
        /// <c>AddBucket()</c> is honoured by the AppHost orchestrator, which creates each bucket
        /// over S3 once the server reports ready -- and that orchestrator only exists in run
        /// mode. Deployed, nothing created them: the server came up on an empty volume, every
        /// receipt upload was refused with <c>NoSuchBucket</c>, and the API reported it as an
        /// internal error. This is the gap
        /// <see cref="PostgresDeploymentExtensions.AsDeployedPostgres"/> fills for
        /// <c>AddDatabase()</c>, but RustFS has no <c>/docker-entrypoint-initdb.d</c> to drop a
        /// script into and no flag or variable that declares a bucket, so the entrypoint is
        /// wrapped and the bucket created over S3 like the orchestrator would have.
        /// </para>
        /// <para>
        /// The script ships as a Compose config and the image is used as it comes: a derived
        /// image would drag the whole RustFS base layer back through the registry, which is what
        /// <see cref="DeploymentExtensions.WithFiles"/> exists to avoid.
        /// </para>
        /// <para>
        /// The healthcheck reports ready only once the buckets exist, so dependents wait for
        /// usable storage rather than merely a listening port -- the window between the two is
        /// exactly when they would otherwise start and fail.
        /// </para>
        /// </summary>
        public IResourceBuilder<RustFsResource> AsDeployedRustFs(params string[] buckets)
        {
            ArgumentOutOfRangeException.ThrowIfZero(buckets.Length, nameof(buckets));

            foreach (var bucket in buckets)
            {
                if (string.IsNullOrWhiteSpace(bucket) || bucket.Any(char.IsWhiteSpace))
                {
                    throw new ArgumentException(
                        $"The buckets reach the container as a space-separated list, so '{bucket}' "
                        + "cannot be named there.",
                        nameof(buckets));
                }
            }

            return storage
                .WithFiles("Assets/rustfs/create-buckets.sh", EntrypointPath, escapeComposeInterpolation: true)
                .WithEnvironment(BucketsVariable, string.Join(' ', buckets))
                .PublishAsDockerComposeService((_, service) =>
                    service.Entrypoint = ["/bin/sh", EntrypointPath])
                .WithComposeHealthcheck(new Healthcheck
                {
                    // Both halves matter: the marker alone would still report ready after the
                    // server died, and the request alone reports ready before the buckets exist.
                    Test =
                    [
                        "CMD-SHELL",
                        "test -f /tmp/buckets-ready "
                        + "&& curl -s -o /dev/null \"http://127.0.0.1:$${RUSTFS_ADDRESS##*:}/\""
                    ],
                    Interval = "5s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "10s"
                });
        }

        /// <summary>
        /// Serves the RustFS console on the console port.
        /// <para>
        /// Without this the port is bound but no console is registered behind it, so a request
        /// falls through to the S3 handler and is answered with an <c>AccessDenied</c> document
        /// rather than a page -- which reads like a permissions problem and is not one.
        /// </para>
        /// <para>
        /// Kept off the host deliberately. The console signs in with the storage credentials and
        /// then has the run of every bucket, so it is reachable only from inside the Compose
        /// network; an operator who needs it forwards a port over SSH. That is also why this is
        /// separate from <c>AsDeployedRustFs</c>: provisioning a bucket the API cannot work
        /// without is not the same decision as serving an administrative UI.
        /// </para>
        /// </summary>
        public IResourceBuilder<RustFsResource> WithRustFsConsole()
            => storage.WithEnvironment(ConsoleVariable, "true");
    }
}

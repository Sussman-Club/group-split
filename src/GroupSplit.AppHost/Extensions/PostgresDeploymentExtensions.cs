using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace GroupSplit.AppHost.Extensions;

public static class PostgresDeploymentExtensions
{
    extension(IResourceBuilder<PostgresServerResource> dbServer)
    {
        /// <summary>
        /// Prepares the Postgres server to run outside local development.
        /// <para>
        /// Aspire creates <c>AddDatabase()</c> databases through the AppHost orchestrator,
        /// which only exists in run mode. A deployed container just runs whatever is in
        /// <c>/docker-entrypoint-initdb.d</c> on first init, so the databases are created by
        /// a script shipped there instead -- as a Compose config rather than baked into a
        /// derived image, which would drag the whole base image through the registry on
        /// every deploy.
        /// </para>
        /// <para>
        /// The healthcheck is what lets Keycloak and the migrations wait for Postgres to
        /// answer rather than merely to have started; see <c>WithComposeDefaults</c> for
        /// how the <c>depends_on</c> edges pick it up.
        /// </para>
        /// </summary>
        public IResourceBuilder<PostgresServerResource> AsDeployedPostgres()
        {
            return dbServer
                .WithFiles(
                    "Assets/postgres/create-databases.sql",
                    "/docker-entrypoint-initdb.d/10-create-databases.sql")
                .WithComposeHealthcheck(new Healthcheck
                {
                    Test = ["CMD-SHELL", "pg_isready -U postgres -d postgres"],
                    Interval = "5s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "10s"
                });
        }
    }
}

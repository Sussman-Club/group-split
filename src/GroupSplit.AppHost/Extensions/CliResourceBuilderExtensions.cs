namespace GroupSplit.AppHost.Extensions;

#pragma warning disable ASPIREPROJECTS001

/// <summary>The command-line client, modelled so it can be run against the local stack.</summary>
public class CliResource(string name) : ProjectResource(name);

public static class CliResourceBuilderExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the CLI as an explicit-start resource.
        /// <para>
        /// It is not a service and nothing waits on it: the value of having it in the model
        /// is that <c>WithReference</c> hands it the API and realm URLs this run happens to
        /// have, so a developer never pastes a port number, and its requests show up in the
        /// dashboard's traces beside the API spans they caused.
        /// </para>
        /// <para>
        /// Run mode only. A deployment has no CLI to orchestrate -- the binary is installed
        /// on whatever machine wants it and is pointed at the public origin.
        /// </para>
        /// </summary>
        public IResourceBuilder<CliResource> AddCli<TProject>([ResourceName] string name)
            where TProject : IProjectMetadata, new()
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(name);

            var project = new CliResource(name);

            return builder
                .AddResource(project)
                .WithAnnotation(new TProject())
                .WithProjectDefaults(new ProjectResourceOptions())
                .WithExplicitStart()
                .WithIconName("WindowConsole");
        }
    }

    extension(IResourceBuilder<CliResource> resourceBuilder)
    {
        /// <summary>
        /// Points the CLI at this run's API and realm.
        /// <para>
        /// The two are set separately rather than through a single server origin because
        /// locally they are separate origins on separate ports: only a deployment puts the
        /// API behind the web app's <c>/api</c> forwarder and Keycloak under <c>/idp</c>,
        /// which is the arrangement the CLI's own URL derivation assumes.
        /// </para>
        /// </summary>
        public IResourceBuilder<CliResource> WithGroupSplitEndpoints(
            IResourceBuilder<ProjectResource> api,
            IResourceBuilder<KeycloakResource> keycloak)
        {
            return resourceBuilder
                .WithReference(api)
                .WaitFor(api)
                .WithEnvironment("GROUPSPLIT_API_URL", api.GetEndpoint("https"))
                .WithEnvironment(context =>
                {
                    var endpoint = keycloak.GetEndpoint("http");

                    context.EnvironmentVariables["GROUPSPLIT_AUTHORITY"] =
                        ReferenceExpression.Create($"{endpoint}/realms/group-split");
                });
        }
    }
}

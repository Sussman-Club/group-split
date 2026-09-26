using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Extensions;

public static class KeycloakRealmResourceBuilderExtensions
{
    /// <summary>
    /// The bootstrap admin Keycloak creates when the app model names none. Matches the
    /// default behind <c>KC_BOOTSTRAP_ADMIN_USERNAME</c>.
    /// </summary>
    private const string DefaultKeycloakAdminUser = "admin";

    /// <summary>
    /// The placeholder <c>realms.json</c> builds the web app's redirect URIs from. Keycloak
    /// resolves it itself on a first-boot import; the realm worker resolves it when it
    /// reconciles a realm that already exists. Both have to be given the same value.
    /// <para>
    /// Named after the <c>web-hostname</c> parameter's Compose variable on purpose. Deployed,
    /// the realm file travels as an inline Compose config, and Compose interpolates every
    /// <c>${...}</c> in it before Keycloak sees it -- from its own variables, not the
    /// container's. A name Compose did not know would arrive as an empty string, and the first
    /// import would store <c>/signin-oidc</c> as the only redirect URI.
    /// </para>
    /// </summary>
    public const string WebHostnameVariable = "WEB_HOSTNAME";

    extension<T>(IResourceBuilder<T> builder) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// Tells Keycloak, or the realm worker, the one origin the <c>web-app</c> client may
        /// send a browser back to. Locally that is the web app's HTTPS endpoint, whose port the
        /// test host moves on every run; deployed it is the public origin.
        /// </summary>
        public IResourceBuilder<T> WithWebAppOrigin(EndpointReference origin)
            => builder.WithEnvironment(WebHostnameVariable, origin);

        /// <inheritdoc cref="WithWebAppOrigin(IResourceBuilder{T}, EndpointReference)"/>
        public IResourceBuilder<T> WithWebAppOrigin(IResourceBuilder<ParameterResource> origin)
            => builder.WithEnvironment(WebHostnameVariable, origin);
    }

    extension<TProject>(IResourceBuilder<TProject> builder) where TProject : ProjectResource
    {
        /// <summary>Passes Keycloak's own bootstrap-admin credentials to an admin worker.</summary>
        public IResourceBuilder<TProject> WithKeycloakAdminAccess(
            IResourceBuilder<KeycloakResource> keycloak)
        {
            builder
                .WithEnvironment("Keycloak__AdminPassword",
                    ReferenceExpression.Create($"{keycloak.Resource.AdminPasswordParameter}"));

            // The username parameter is null unless one was passed to AddKeycloak, and this
            // model does not pass one -- so the container takes Keycloak's own default and
            // both workers have to be told the same name. The password is always a parameter,
            // generated when it is not supplied, so it needs no such fallback.
            return keycloak.Resource.AdminUserNameParameter is { } adminUser
                ? builder.WithEnvironment("Keycloak__AdminUser",
                    ReferenceExpression.Create($"{adminUser}"))
                : builder.WithEnvironment("Keycloak__AdminUser", DefaultKeycloakAdminUser);
        }

        public IResourceBuilder<TProject> WithLocalRealmResetCommand(
            IResourceBuilder<KeycloakResource> keycloak,
            IResourceBuilder<ProjectResource> resetWorker)
        {
            return builder.WithCommand(
                name: "reset-realm",
                displayName: "Reset local Keycloak realm",
                executeCommand: async context =>
                {
                    var commands = context.Services.GetRequiredService<ResourceCommandService>();

                    var reset = await commands.ExecuteCommandAsync(
                        resetWorker.Resource, "resource-start", context.CancellationToken);
                    if (!reset.Success) return reset;

                    var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
                    var state = await notifications.WaitForResourceAsync(
                        resetWorker.Resource.Name,
                        [KnownResourceStates.Finished, KnownResourceStates.Exited,
                            KnownResourceStates.FailedToStart],
                        context.CancellationToken);
                    if (state != KnownResourceStates.Finished)
                    {
                        throw new InvalidOperationException(
                            $"The realm reset worker ended in state '{state}'; Keycloak was not restarted.");
                    }

                    var stop = await commands.ExecuteCommandAsync(
                        keycloak.Resource, "resource-stop", context.CancellationToken);
                    if (!stop.Success) return stop;

                    return await commands.ExecuteCommandAsync(
                        keycloak.Resource, "resource-start", context.CancellationToken);
                },
                new CommandOptions
                {
                    IconName = "BroomSparkle",
                    ConfirmationMessage =
                        "This deletes the local Keycloak realm and all of its users. Keycloak will restart and import the checked-in realm definition. Continue?"
                });
        }
    }
}

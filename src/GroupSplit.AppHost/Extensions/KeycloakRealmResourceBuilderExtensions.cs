using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Extensions;

/// <summary>A variable both Keycloak's first-boot import and the realm worker resolve.</summary>
internal sealed record RealmVariableAnnotation(string Name, ReferenceExpression Value) : IResourceAnnotation;

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

        /// <summary>
        /// Sets a variable a managed setting in <c>realms.json</c> interpolates, and marks it so
        /// that <see cref="WithRealmVariablesOf"/> hands the realm worker the same value.
        /// </summary>
        /// <remarks>
        /// For the switches that decide a managed realm setting -- <c>SMTP_ENABLED</c>
        /// deciding <c>resetPasswordAllowed</c>, say. Keycloak resolves them on a first-boot
        /// import and the worker on every start after, and the two must never disagree.
        /// </remarks>
        public IResourceBuilder<T> WithRealmVariable(string name, ReferenceExpression value)
            => builder
                .WithAnnotation(new RealmVariableAnnotation(name, value))
                .WithEnvironment(name, value);

        /// <summary>
        /// Gives the realm worker every variable <paramref name="keycloak"/> was given through
        /// <see cref="WithRealmVariable"/>, and nothing else of its environment: the worker has
        /// no use for the mail relay's password.
        /// </summary>
        /// <remarks>
        /// Read when the worker's environment is built rather than now, so a variable Keycloak
        /// is given after this call -- by the run- or publish-mode branch of the AppHost -- is
        /// still passed on.
        /// </remarks>
        public IResourceBuilder<T> WithRealmVariablesOf(IResourceBuilder<KeycloakResource> keycloak)
            => builder.WithEnvironment(context =>
            {
                foreach (var variable in keycloak.Resource.Annotations.OfType<RealmVariableAnnotation>())
                {
                    context.EnvironmentVariables[variable.Name] = variable.Value;
                }
            });
    }

    extension<TProject>(IResourceBuilder<TProject> builder) where TProject : IResourceWithEnvironment
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
            IResourceBuilder<IResource> resetWorker)
        {
            return builder.WithCommand(
                name: "reset-realm",
                displayName: "Reset local Keycloak realm",
                executeCommand: async context =>
                {
                    var commands = context.Services.GetRequiredService<ResourceCommandService>();
                    var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

                    // Only a run that stopped after this start counts. A second reset would
                    // otherwise be answered by the snapshot the first one left behind, and
                    // Keycloak would restart while the realm was still being deleted.
                    var requestedAt = DateTime.UtcNow;

                    var reset = await commands.ExecuteCommandAsync(
                        resetWorker.Resource, "resource-start", context.CancellationToken);
                    if (!reset.Success) return reset;

                    var ended = await notifications.WaitForResourceAsync(
                        resetWorker.Resource.Name,
                        resource => resource.Snapshot.StopTimeStamp > requestedAt
                                    || resource.Snapshot.State?.Text == KnownResourceStates.FailedToStart,
                        context.CancellationToken);

                    // A project that exits non-zero still ends in Finished, so the exit code is
                    // what says whether the realm was actually deleted.
                    if (ended.Snapshot.State?.Text != KnownResourceStates.Finished || ended.Snapshot.ExitCode != 0)
                    {
                        return CommandResults.Failure(
                            $"The realm reset worker ended in state '{ended.Snapshot.State?.Text}' with exit code "
                            + $"{ended.Snapshot.ExitCode?.ToString() ?? "none"}; Keycloak was not restarted. "
                            + "See the realm-reset logs.");
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

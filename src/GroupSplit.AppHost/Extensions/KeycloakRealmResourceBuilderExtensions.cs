using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Extensions;

public static class KeycloakRealmResourceBuilderExtensions
{
    /// <summary>
    /// The bootstrap admin Keycloak creates when the app model names none. Matches the
    /// default behind <c>KC_BOOTSTRAP_ADMIN_USERNAME</c>.
    /// </summary>
    private const string DefaultKeycloakAdminUser = "admin";

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

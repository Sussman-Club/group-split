namespace GroupSplit.AppHost.Extensions;

public static class KeycloakDevelopmentExtensions
{
    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Prepares Keycloak for local development. The run-mode counterpart of
        /// <see cref="KeycloakDeploymentExtensions.AsDeployedKeycloak"/>.
        /// <para>
        /// The realm and theme are mounted straight from the checkout: the orchestrator runs
        /// on the machine that has the files, which is the one assumption the deployed
        /// shape cannot make. Mail goes to Mailpit, so a reset or verification mail can be
        /// read rather than taken on trust, and the data volume keeps the realm's users
        /// across restarts so nobody signs up again every morning.
        /// </para>
        /// <para>
        /// Keycloak does its own <c>${...}</c> substitution on the imported realm here,
        /// reading the container's environment. That is why <c>WithSmtp(mailpit)</c> sets
        /// literals where the deployed shape sets parameters.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> AsDevelopmentKeycloak(
            IResourceBuilder<MailPitContainerResource> mailpit)
        {
            return keycloak
                .WithRealmImport("./Assets/keycloak/realms.json")
                .WithContainerFiles("/opt/keycloak/themes/group-split", "./Assets/keycloak/themes")
                .WithSmtp(mailpit)
                .WithDataVolume();
        }
    }
}

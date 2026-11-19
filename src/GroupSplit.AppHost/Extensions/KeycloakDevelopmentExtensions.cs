namespace GroupSplit.AppHost.Extensions;

#pragma warning disable ASPIREPERSISTENCE001

public static class KeycloakDevelopmentExtensions
{
    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Prepares Keycloak for local development. The run-mode counterpart of
        /// <see cref="KeycloakDeploymentExtensions.AsDeployedKeycloak"/>.
        /// <para>
        /// The theme is supplied from the checkout and the realm import is adapted for
        /// Mailpit. Mail goes to Mailpit, so a reset or verification mail can be
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
            // The production relay uses authentication and STARTTLS. Mailpit is a local,
            // unauthenticated SMTP server, so use a run-mode copy of the realm import.
            var realmPath = Path.Combine(keycloak.ApplicationBuilder.AppHostDirectory,
                "Assets", "keycloak", "realms.json");
            var realm = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(realmPath))!.AsObject();
            var smtp = realm["smtpServer"]?.AsObject()
                       ?? throw new InvalidOperationException("The Keycloak realm has no SMTP configuration.");
            smtp["starttls"] = "false";
            smtp["auth"] = "false";

            return keycloak
                .WithContainerFiles("/opt/keycloak/data/import",
                    [new ContainerFile { Name = "realms.json", Contents = realm.ToJsonString() }])
                .WithContainerFiles("/opt/keycloak/themes/group-split", "./Assets/keycloak/themes")
                .WithSmtp(mailpit)
                .WithDataVolume();
        }
    }
}

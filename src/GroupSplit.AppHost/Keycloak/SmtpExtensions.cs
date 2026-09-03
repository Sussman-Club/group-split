using Microsoft.Extensions.Configuration;

namespace GroupSplit.AppHost.Keycloak;

public static class SmtpExtensions
{
    private const string HostConfigurationKey = "Smtp:Host";

    private const string PortConfigurationKey = "Smtp:Port";

    private const string FromConfigurationKey = "Smtp:From";

    private const string UsernameConfigurationKey = "Smtp:Username";

    private const string PasswordConfigurationKey = "Smtp:Password";

    private const string StartTlsConfigurationKey = "Smtp:StartTls";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Points the imported realm at an SMTP server.
        /// <para>
        /// Keycloak substitutes these into the <c>${...}</c> placeholders in
        /// realms.json at import time, so no credential is committed and the
        /// host is not pinned to the local MailPit catcher. Locally the
        /// Development configuration supplies MailPit; deployments supply a
        /// real server through the <c>smtp-*</c> parameters.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp(IConfiguration configuration)
        {
            var hostValue = configuration[HostConfigurationKey] ?? string.Empty;
            var portValue = configuration[PortConfigurationKey] ?? string.Empty;
            // Keycloak refuses to import a realm whose smtpServer has an empty sender, so this
            // needs a usable default even when no relay is configured.
            var fromValue = configuration[FromConfigurationKey] ?? "no-reply@groupsplit.local";
            var usernameValue = configuration[UsernameConfigurationKey] ?? string.Empty;
            var passwordValue = configuration[PasswordConfigurationKey] ?? string.Empty;

            // MailPit accepts anonymous mail; a real relay almost always needs credentials.
            var authenticated = !string.IsNullOrWhiteSpace(usernameValue);

            var startTlsValue = configuration[StartTlsConfigurationKey] ?? (authenticated ? "true" : "false");

            // Parameters rather than raw strings: the dashboard masks the secret
            // and the values flow into the manifest when this model is published.
            var host = keycloak.ApplicationBuilder
                .AddParameter("smtp-host", () => hostValue);

            var port = keycloak.ApplicationBuilder
                .AddParameter("smtp-port", () => portValue);

            var from = keycloak.ApplicationBuilder
                .AddParameter("smtp-from", () => fromValue);

            var username = keycloak.ApplicationBuilder
                .AddParameter("smtp-username", () => usernameValue);

            var password = keycloak.ApplicationBuilder
                .AddParameter("smtp-password", () => passwordValue, secret: true);

            // Parameters too, so a deployment can turn on auth/TLS without an AppHost rebuild.
            var auth = keycloak.ApplicationBuilder
                .AddParameter("smtp-auth", () => authenticated ? "true" : "false");

            var startTls = keycloak.ApplicationBuilder
                .AddParameter("smtp-starttls", () => startTlsValue);

            return keycloak
                .WithEnvironment("GS_SMTP_HOST", host)
                .WithEnvironment("GS_SMTP_PORT", port)
                .WithEnvironment("GS_SMTP_FROM", from)
                .WithEnvironment("GS_SMTP_AUTH", auth)
                .WithEnvironment("GS_SMTP_USER", username)
                .WithEnvironment("GS_SMTP_PASSWORD", password)
                .WithEnvironment("GS_SMTP_STARTTLS", startTls);
        }
    }
}

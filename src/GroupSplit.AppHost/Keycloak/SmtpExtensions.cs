using GroupSplit.AppHost.Mail;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.AppHost.Keycloak;

public static class SmtpExtensions
{
    // Deploy-time values arrive as Aspire parameters. The deploy workflow turns every
    // GitHub secret and variable into Parameters__<kebab-name>, so SMTP_HOST, SMTP_FROM,
    // SMTP_USER and SMTP_PASSWORD there land on the names below with no extra wiring.
    private const string HostParameterName = "smtp-host";

    private const string PortParameterName = "smtp-port";

    private const string FromParameterName = "smtp-from";

    private const string UserParameterName = "smtp-user";

    private const string PasswordParameterName = "smtp-password";

    // Deliberately not an SMTP_ name: it is a realm policy rather than part of the relay.
    private const string VerifyEmailParameterName = "verify-email";

    // Submission with STARTTLS. Port 25 is blocked outbound by most hosts, and 465 wants
    // implicit TLS, which is the realm's `ssl` flag rather than its `starttls` one.
    private const string DefaultPort = "587";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Points the realm's SMTP server at a relay described by deployment parameters.
        /// <para>
        /// Keycloak substitutes these into the <c>${...}</c> placeholders in the
        /// <c>smtpServer</c> block of realms.json at import time, so no credential is
        /// committed. Provider-agnostic on purpose -- host, port, user and sender are all
        /// parameters -- so changing relay is a secret change rather than a code one.
        /// </para>
        /// <para>
        /// With no relay configured this degrades to SMTP disabled rather than failing the
        /// deploy, and registration keeps working rather than trapping every new user
        /// behind a verification mail that could never be delivered.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp(IConfiguration configuration)
        {
            var host = configuration[$"Parameters:{HostParameterName}"];
            var from = configuration[$"Parameters:{FromParameterName}"];
            var user = configuration[$"Parameters:{UserParameterName}"];
            var password = configuration[$"Parameters:{PasswordParameterName}"];

            var configured = !string.IsNullOrWhiteSpace(host)
                             && !string.IsNullOrWhiteSpace(from)
                             && !string.IsNullOrWhiteSpace(user)
                             && !string.IsNullOrWhiteSpace(password);

            if (!configured)
                return keycloak.WithSmtp(SmtpConfiguration.Disabled, verifyEmail: false);

            var builder = keycloak.ApplicationBuilder;

            // Parameters rather than raw strings: the dashboard masks the password and the
            // values reach the manifest when this model is published. Declared without a
            // value callback for everything proven present above, so Aspire owns
            // resolution; the port is the one value worth defaulting.
            var portValue = configuration[$"Parameters:{PortParameterName}"];

            var smtp = new SmtpConfiguration(
                Enabled: true,
                Host: Parameter(builder.AddParameter(HostParameterName)),
                Port: Parameter(builder.AddParameter(
                    PortParameterName,
                    () => string.IsNullOrWhiteSpace(portValue) ? DefaultPort : portValue)),
                From: Parameter(builder.AddParameter(FromParameterName)),
                User: Parameter(builder.AddParameter(UserParameterName)),
                Password: Parameter(builder.AddParameter(PasswordParameterName, secret: true)),
                Auth: true,
                StartTls: true);

            // Having a relay and trusting it are separate decisions. A sender domain part
            // way through verification at the provider has every send rejected, and turning
            // verification on then would strand existing users at their next login behind a
            // mail that cannot arrive. So it is opt-in, once mail is really flowing.
            var requested = configuration[$"Parameters:{VerifyEmailParameterName}"];

            return keycloak.WithSmtp(
                smtp, verifyEmail: bool.TryParse(requested, out var parsed) && parsed);
        }

        /// <summary>
        /// Points the realm's SMTP server at Mailpit.
        /// <para>
        /// Mailpit accepts everything and delivers nothing, so local development needs
        /// neither a relay account nor a secret, and a reset or verification mail can be
        /// read in Mailpit's inbox rather than taken on trust.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp(
            IResourceBuilder<MailPitContainerResource> mailpit)
        {
            var endpoint = mailpit.Resource.PrimaryEndpoint;

            var smtp = new SmtpConfiguration(
                Enabled: true,
                Host: ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Host)}"),
                Port: ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Port)}"),
                // Mailpit accepts any sender, but Keycloak still puts this in the From
                // header. `.localhost` is reserved, so a stray real send cannot resolve.
                From: ReferenceExpression.Create($"no-reply@group-split.localhost"),
                User: ReferenceExpression.Create($""),
                Password: ReferenceExpression.Create($""),
                Auth: false,
                StartTls: false);

            // On locally, where the verification mail is one click away in the inbox.
            return keycloak
                .WithSmtp(smtp, verifyEmail: true)
                .WaitFor(mailpit);
        }

        private IResourceBuilder<KeycloakResource> WithSmtp(
            SmtpConfiguration smtp,
            bool verifyEmail) =>
            keycloak
                .WithSmtpEnvironment(smtp)
                // Verification can never be on without a relay: Keycloak would raise the
                // required action and then have nowhere to send the mail that clears it.
                .WithEnvironment("GS_VERIFY_EMAIL", smtp.Enabled && verifyEmail ? "true" : "false");
    }

    private static ReferenceExpression Parameter(IResourceBuilder<ParameterResource> parameter) =>
        ReferenceExpression.Create($"{parameter.Resource}");
}

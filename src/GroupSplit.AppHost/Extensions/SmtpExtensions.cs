#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

public static class SmtpExtensions
{
    // Deploy-time values arrive as Aspire parameters. The deploy workflow turns every GitHub
    // secret and variable into Parameters__<lower-cased name>, so SMTP_ENABLED, SMTP_HOST and
    // the rest land on the names below with no extra wiring. These names also decide what
    // realms.json can interpolate -- see WithSmtpEnvironment.
    private const string EnabledParameterName = "smtp-enabled";

    private const string HostParameterName = "smtp-host";

    private const string PortParameterName = "smtp-port";

    private const string FromParameterName = "smtp-from";

    private const string UserParameterName = "smtp-user";

    private const string PasswordParameterName = "smtp-password";

    // Submission with STARTTLS. Port 25 is blocked outbound by most hosts, and 465 wants
    // implicit TLS, which is the realm's `ssl` flag rather than its `starttls` one.
    private const string DefaultPort = "587";

    // Keycloak validates the sender address while importing the realm and refuses to
    // start on one it cannot parse -- an empty string included. So the unconfigured case
    // still needs a syntactically valid address, and .invalid is reserved by RFC 2606
    // precisely so that it can never resolve. With no host to connect to, nothing sends.
    private const string UnroutableSender = "no-reply@group-split.invalid";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Points the realm's SMTP server at a relay described by deployment parameters.
        /// <para>
        /// <c>smtp-enabled</c> is the switch; host, port, sender, user and password describe
        /// the relay. All are optional, so a deployment that never mentions mail resolves to
        /// a disabled relay rather than a prompt, and every placeholder in realms.json still
        /// has a defined value. That last part is not cosmetic: Compose turns an undefined
        /// name into a blank string, and Keycloak refuses to start on a blank sender.
        /// </para>
        /// <para>
        /// Provider-agnostic on purpose, so changing relay is a variable change rather than
        /// a code one. The switch is a parameter of its own rather than inferred from the
        /// credentials being present, so this method only declares parameters and passes
        /// them through. What the values have to agree on -- an enabled relay is complete --
        /// is checked once they are resolved, in the <c>validate-smtp</c> pipeline step.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp()
        {
            var builder = keycloak.ApplicationBuilder;

            var enabled = builder.AddOptionalParameter(EnabledParameterName, "false")
                .WithDescription(
                    "Whether Keycloak sends mail (true/false). "
                    + "Needs smtp-host, smtp-from, smtp-user and smtp-password when true.");

            var host = builder.AddOptionalParameter(HostParameterName, string.Empty)
                .WithDescription("Relay hostname, e.g. smtp.resend.com.");

            var port = builder.AddOptionalParameter(PortParameterName, DefaultPort)
                .WithDescription(
                    "Relay port. 587 is submission over STARTTLS; 465 wants implicit TLS, "
                    + "which is the realm's ssl flag rather than its starttls one.");

            var from = builder.AddOptionalParameter(FromParameterName, UnroutableSender)
                .WithDescription(
                    "Sender address, on a domain the relay has verified. "
                    + "Keycloak refuses to start on one it cannot parse.");

            var user = builder.AddOptionalParameter(UserParameterName, string.Empty)
                .WithDescription("Relay username.");

            var password = builder.AddOptionalParameter(PasswordParameterName, string.Empty, secret: true)
                .WithDescription("Relay password or API key.");

            builder.Pipeline.AddStep(
                "validate-smtp",
                context => enabled.RequireValuesWhenEnabledAsync(context, [host, from, user, password]),
                dependsOn: WellKnownPipelineSteps.ProcessParameters,
                requiredBy: WellKnownPipelineSteps.BuildPrereq);

            return keycloak.WithSmtpEnvironment(new SmtpConfiguration(
                Enabled: Reference(enabled),
                Host: Reference(host),
                Port: Reference(port),
                From: Reference(from),
                User: Reference(user),
                Password: Reference(password)));
        }

        /// <summary>
        /// Points the realm's SMTP server at Mailpit.
        /// <para>
        /// Mailpit accepts everything and delivers nothing, so local development needs
        /// neither a relay account nor a secret, and a reset or verification mail can be
        /// read in Mailpit's inbox rather than taken on trust. Plain literals rather than
        /// parameters, because run mode mounts realms.json and leaves the substitution to
        /// Keycloak, reading the container's own environment.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp(
            IResourceBuilder<MailPitContainerResource> mailpit)
        {
            var endpoint = mailpit.Resource.PrimaryEndpoint;

            var smtp = new SmtpConfiguration(
                Enabled: Literal("true"),
                Host: ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Host)}"),
                Port: ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Port)}"),
                From: Literal("no-reply@group-split.localhost"),
                User: Literal(string.Empty),
                Password: Literal(string.Empty));

            return keycloak
                .WithSmtpEnvironment(smtp)
                .WaitFor(mailpit);
        }
    }

    private static ReferenceExpression Literal(string value) =>
        ReferenceExpression.Create($"{value}");

    private static ReferenceExpression Reference(IResourceBuilder<ParameterResource> parameter) =>
        ReferenceExpression.Create($"{parameter.Resource}");
}
